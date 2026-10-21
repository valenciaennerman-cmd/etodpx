// Motor: filtre kurulumu, tutamaklar, is parcaciklari.
//
// Butun tasarim cekirdek filtresini dar tutmak uzerine kurulu. Kullanici alanina
// yalnizca yeni bir baglantinin ilk paketi geliyor (ClientHello / HTTP istek
// satiri / QUIC Initial); ping, oyun trafigi, indirme ve yukleme cekirdegi hic
// terk etmiyor.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Etodpx.Engine
{
    internal sealed unsafe class DpiEngine : IDisposable
    {
        private const int ErrorFileNotFound = 2;
        private const int ErrorAccessDenied = 5;
        private const int ErrorInvalidParameter = 87;
        private const int ErrorNoData = 232;
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorInvalidImageHash = 577;
        private const int ErrorServiceDoesNotExist = 1060;
        private const int ErrorDriverBlocked = 1275;
        private const int ErrorServiceDisabled = 1058;

        private readonly Config _cfg;
        private readonly Stats _stats = new Stats();

        private IntPtr _tcpHandle = WinDivert.InvalidHandle;
        private IntPtr _quicHandle = WinDivert.InvalidHandle;
        // Parcali ClientHello'nun devam segmentleri icin SNIFF tutamagi.
        private IntPtr _contHandle = WinDivert.InvalidHandle;
        private readonly List<Thread> _threads = new List<Thread>();
        private readonly List<DesyncWorker> _workers = new List<DesyncWorker>();
        private readonly List<Hostlist> _retiredHosts = new List<Hostlist>();
        private Hostlist _hosts;
        private FileSystemWatcher _hostWatcher;
        private int _reloadQueued;
        private ContinuationTable _continuations;
        private volatile bool _running;
        private int _generation;
        private int _faultRaised;
        // Baslatma arka planda yapiliyor; kullanici o sirada pencereyi kaparsa
        // Stop() ile Start() ic ice girebilir. Kilit bunu tekile indirir.
        private readonly object _lifecycle = new object();

        public Stats Stats { get { return _stats; } }
        public bool IsRunning { get { return _running; } }
        public string ActiveTcpFilter { get; private set; }
        // Kac alan adina dokunuluyor; 0 = liste yok, hepsine dokunuluyor.
        public int HostlistCount { get; private set; }
        public string ActiveQuicFilter { get; private set; }
        public string QuicWarning { get; private set; }
        public string ActiveContinuationFilter { get; private set; }
        // Devam tutamagi acilamadiysa koruma yine calisir; yalnizca SNI'si ikinci
        // segmente tasan ClientHello'lar eskisi gibi dokunulmadan gider.
        public string ContinuationWarning { get; private set; }

        // Beklenmedik hatayi arayuze tasir.
        public event Action<string> Fault;
        // hosts.txt canli yenilendiginde arayuze bildirir.
        public event Action<int> HostlistReloaded;

        public DpiEngine(Config cfg) { _cfg = cfg; }

        // --- yasam dongusu

        public void Start()
        {
            lock (_lifecycle) { StartLocked(); }
        }

        private void StartLocked()
        {
            if (_running) return;

            _stats.Reset();
            ActiveTcpFilter = null;
            ActiveQuicFilter = null;
            QuicWarning = null;
            ActiveContinuationFilter = null;
            ContinuationWarning = null;
            Interlocked.Exchange(ref _faultRaised, 0);

            int generation = unchecked(Volatile.Read(ref _generation) + 1);
            Volatile.Write(ref _generation, generation);

            string filter = PickFilter(BuildTcpFilterCandidates(), DivertLayer.Network);
            if (filter == null)
                throw new EngineException("TCP filtresi derlenemedi. WinDivert sürümü çok eski olabilir (2.2+ gerekir).");
            ActiveTcpFilter = filter;

            _tcpHandle = Open(filter);

            bool quicOn = _cfg.QuicMode != "off" && _cfg.QuicMode != "none";
            if (quicOn)
            {
                string qf = PickFilter(BuildQuicFilterCandidates(), DivertLayer.Network);
                if (qf != null)
                {
                    ActiveQuicFilter = qf;
                    try { _quicHandle = Open(qf); }
                    catch (EngineException ex)
                    {
                        _quicHandle = WinDivert.InvalidHandle;
                        QuicWarning = "QUIC başlatılamadı: " + ex.Message;
                    }
                }
                else QuicWarning = "QUIC filtresi sürücü tarafından kabul edilmedi.";
            }

            _hosts = LoadHostlist();
            HostlistCount = _hosts == null ? 0 : _hosts.Count;
            StartHostWatcher();

            // LSO'suz kartlarda (Wi-Fi) yigin ClientHello'yu kendisi boluyor ve SNI
            // ikinci segmente dusebiliyor. Devam segmentlerinin kopyasi olmadan o
            // baglantilara karar verilemiyor. Acilamazsa koruma yine baslar.
            if (WantsContinuations(_hosts != null)) OpenContinuations();

            _running = true;
            _stats.MarkStart();

            int tcpThreads = _cfg.Threads > 0
                ? _cfg.Threads
                : Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

            Hostlist hosts = _hosts;
            ContinuationTable continuations = _continuations;
            for (int i = 0; i < tcpThreads; i++) SpawnWorker(_tcpHandle, false, i, generation, hosts, continuations);
            if (_quicHandle != WinDivert.InvalidHandle) SpawnWorker(_quicHandle, true, 100, generation, hosts, null);
            if (_contHandle != WinDivert.InvalidHandle) SpawnContinuationReader(_contHandle, continuations, generation);
        }

        // SNI yalnizca alan adi listesi ya da alan adina gore bolme icin gerekli.
        private bool WantsContinuations(bool hasHostlist)
        {
            if (!TlsActive()) return false;
            if (hasHostlist) return true;
            string m = _cfg.TlsMode ?? string.Empty;
            int fixedPos;
            return (m.Contains("split") || m.Contains("disorder")) && !int.TryParse(_cfg.SplitPos, out fixedPos);
        }

        private void OpenContinuations()
        {
            string filter = PickFilter(BuildContinuationFilterCandidates(), DivertLayer.Network);
            if (filter == null)
            {
                ContinuationWarning = "Parçalı ClientHello filtresi sürücü tarafından kabul edilmedi.";
                return;
            }

            var table = new ContinuationTable();
            try
            {
                // Once kopyalasin: yuksek oncelikli tutamak paketi ilk gorur, boylece
                // TCP tutamagina yanlislikla takilan bir devam segmenti de kacmaz.
                short priority = (short)Math.Min(30000, _cfg.DivertPriority + 1);
                _contHandle = Open(filter, priority, DivertFlags.Sniff | DivertFlags.RecvOnly);
                _continuations = table;
                ActiveContinuationFilter = filter;
            }
            catch (EngineException ex)
            {
                table.Dispose();
                _contHandle = WinDivert.InvalidHandle;
                ContinuationWarning = "Parçalı ClientHello yakalanamadı: " + ex.Message;
            }
        }

        public void Stop()
        {
            lock (_lifecycle) { StopLocked(); }
        }

        private void StopLocked()
        {
            StopHostWatcher();

            if (!_running && _tcpHandle == WinDivert.InvalidHandle &&
                _quicHandle == WinDivert.InvalidHandle && _contHandle == WinDivert.InvalidHandle) return;
            _running = false;

            if (_tcpHandle != WinDivert.InvalidHandle) WinDivert.WinDivertShutdown(_tcpHandle, WinDivert.ShutdownBoth);
            if (_quicHandle != WinDivert.InvalidHandle) WinDivert.WinDivertShutdown(_quicHandle, WinDivert.ShutdownBoth);
            if (_contHandle != WinDivert.InvalidHandle) WinDivert.WinDivertShutdown(_contHandle, WinDivert.ShutdownBoth);

            // Ortak son tarih; is parcacigi basina beklemek kapanisi uzatirdi.
            var deadline = Environment.TickCount + 1500;
            bool allExited = true;
            foreach (var t in _threads)
            {
                int left = deadline - Environment.TickCount;
                if (left < 0) left = 0;
                try { if (!t.Join(left)) allExited = false; }
                catch { allExited = false; }
            }

            var pending = _threads.ToArray();
            _threads.Clear();
            lock (_workers) { _workers.Clear(); }

            IntPtr tcp = _tcpHandle, quic = _quicHandle, cont = _contHandle;
            Hostlist hosts = _hosts;
            Hostlist[] retired = _retiredHosts.ToArray();
            _retiredHosts.Clear();
            ContinuationTable continuations = _continuations;
            _tcpHandle = WinDivert.InvalidHandle;
            _quicHandle = WinDivert.InvalidHandle;
            _contHandle = WinDivert.InvalidHandle;
            _hosts = null;
            _continuations = null;

            if (allExited)
            {
                CloseHandles(tcp, quic, cont, hosts, retired, continuations);
                return;
            }

            // Bir is parcacigi hala WinDivertRecv icinde. Tutamagi simdi kapatmak
            // onun altindan zemini cekmek olur; kapatmayi arka plana aliyoruz.
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (var t in pending)
                {
                    // Shutdown Recv'i uyandirir; surucu gecikirse yine de bekliyoruz.
                    try { t.Join(); }
                    catch { }
                }
                CloseHandles(tcp, quic, cont, hosts, retired, continuations);
            });
        }

        private static void CloseHandles(IntPtr tcp, IntPtr quic, IntPtr cont, Hostlist hosts,
                                         Hostlist[] retired, ContinuationTable continuations)
        {
            if (tcp != WinDivert.InvalidHandle) WinDivert.WinDivertClose(tcp);
            if (quic != WinDivert.InvalidHandle) WinDivert.WinDivertClose(quic);
            if (cont != WinDivert.InvalidHandle) WinDivert.WinDivertClose(cont);
            // Is parcaciklari cikmadan serbest birakilamaz: sicak yol bu bloklari
            // pointer uzerinden okuyor.
            if (hosts != null) hosts.Dispose();
            if (retired != null)
            {
                foreach (var r in retired) if (r != null) r.Dispose();
            }
            if (continuations != null) continuations.Dispose();
        }

        // --- canli hostlist izleme ve yenileme

        public string ResolvedHostlistPath()
        {
            string path = _cfg.HostlistPath;
            if (string.IsNullOrEmpty(path)) return null;
            if (!Path.IsPathRooted(path))
            {
                string dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                path = Path.Combine(dir, path);
            }
            return path;
        }

        private Hostlist LoadHostlist()
        {
            string path = ResolvedHostlistPath();
            if (string.IsNullOrEmpty(path)) return null;
            return Hostlist.Load(path, _cfg.Warnings);
        }

        // Motor calisirken hosts.txt dosyasini yeniden okur ve is parcaciklarina
        // kesintisiz uygular.
        public int ReloadHostlist()
        {
            lock (_lifecycle)
            {
                string path = ResolvedHostlistPath();
                Hostlist fresh = string.IsNullOrEmpty(path) ? null : Hostlist.Load(path, null);
                Hostlist old = _hosts;
                _hosts = fresh;
                HostlistCount = fresh == null ? 0 : fresh.Count;

                if (_running)
                {
                    if (old != null) _retiredHosts.Add(old);
                    lock (_workers)
                    {
                        foreach (var w in _workers) w.SetHostlist(fresh);
                    }
                }
                else
                {
                    if (old != null) old.Dispose();
                }

                var cb = HostlistReloaded;
                if (cb != null) { try { cb(HostlistCount); } catch { } }
                return HostlistCount;
            }
        }

        private void StartHostWatcher()
        {
            StopHostWatcher();
            try
            {
                string path = ResolvedHostlistPath();
                if (string.IsNullOrEmpty(path)) return;
                string dir = Path.GetDirectoryName(path);
                string file = Path.GetFileName(path);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file) || !Directory.Exists(dir)) return;

                var watcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                FileSystemEventHandler onChange = delegate { ScheduleHostReload(); };
                RenamedEventHandler onRename = delegate { ScheduleHostReload(); };
                watcher.Changed += onChange;
                watcher.Created += onChange;
                watcher.Deleted += onChange;
                watcher.Renamed += onRename;
                watcher.EnableRaisingEvents = true;
                _hostWatcher = watcher;
            }
            catch { _hostWatcher = null; }
        }

        private void StopHostWatcher()
        {
            var w = _hostWatcher;
            _hostWatcher = null;
            if (w != null)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
        }

        private void ScheduleHostReload()
        {
            if (!_running) return;
            if (Interlocked.CompareExchange(ref _reloadQueued, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(250);
                    Interlocked.Exchange(ref _reloadQueued, 0);
                    if (_running) ReloadHostlist();
                }
                catch { Interlocked.Exchange(ref _reloadQueued, 0); }
            });
        }

        public void Dispose() { Stop(); }

        // --- tutamak

        private IntPtr Open(string filter)
        {
            return Open(filter, _cfg.DivertPriority, DivertFlags.None);
        }

        private IntPtr Open(string filter, short priority, DivertFlags flags)
        {
            IntPtr h;
            try
            {
                h = WinDivert.WinDivertOpen(filter, DivertLayer.Network, priority, (ulong)flags);
            }
            catch (DllNotFoundException)
            {
                throw new EngineException(
                    "WinDivert.dll bulunamadı.\n\nbin klasörüne WinDivert.dll ve WinDivert64.sys " +
                    "dosyalarını koyun (kurulum: tools\\setup-windivert.cmd).");
            }
            catch (BadImageFormatException)
            {
                throw new EngineException("WinDivert.dll mimarisi uyumsuz. 64 bit sürüm gerekli.");
            }

            if (h == WinDivert.InvalidHandle)
            {
                int err = Marshal.GetLastWin32Error();
                throw new EngineException(DescribeOpenError(err));
            }

            if (!WinDivert.WinDivertSetParam(h, DivertParam.QueueLength, (ulong)_cfg.QueueLength) ||
                !WinDivert.WinDivertSetParam(h, DivertParam.QueueTime, (ulong)_cfg.QueueTime) ||
                !WinDivert.WinDivertSetParam(h, DivertParam.QueueSize, (ulong)_cfg.QueueSize))
            {
                int err = Marshal.GetLastWin32Error();
                WinDivert.WinDivertClose(h);
                throw new EngineException("WinDivert kuyruk ayarları uygulanamadı (hata " + err + ").");
            }
            return h;
        }

        private static string DescribeOpenError(int err)
        {
            switch (err)
            {
                case ErrorFileNotFound:
                    return "WinDivert sürücü dosyası (WinDivert64.sys) bulunamadı.\n" +
                           "DLL ile .sys dosyası aynı klasörde olmalı.";
                case ErrorAccessDenied:
                    return "Yönetici izni gerekiyor. etodpx yönetici olarak çalıştırılmalı.";
                case ErrorInvalidParameter:
                    return "Filtre ifadesi sürücü tarafından reddedildi (WinDivert 2.2+ gerekir).";
                case ErrorInvalidImageHash:
                    return "WinDivert sürücüsünün imzası Windows tarafından kabul edilmedi.\n" +
                           "Resmi WinDivert 2.2.2 paketini kullanın.";
                case ErrorServiceDoesNotExist:
                    return "WinDivert servisi kurulamadı. Bir güvenlik yazılımı engelliyor olabilir.\n" +
                           "Antivirüsü geçici kapatıp tekrar deneyin.";
                case ErrorDriverBlocked:
                    return "Sürücü Windows tarafından engellendi.\n" +
                           "Windows Güvenliği > Cihaz güvenliği > Çekirdek yalıtımı >\n" +
                           "Bellek bütünlüğü açıksa kapatıp bilgisayarı yeniden başlatın.";
                case ErrorServiceDisabled:
                    // Onceki bir GoodbyeDPI/zapret kurulumundan ya da antivirusten
                    // kalan devre disi servis. Silinince WinDivert kendini kurar.
                    return "Makinede devre dışı bırakılmış eski bir WinDivert servisi var.\n\n" +
                           "Yönetici komut isteminde şunu çalıştırıp etodpx'i yeniden açın:\n" +
                           "    sc stop WinDivert\n" +
                           "    sc delete WinDivert";
                default:
                    return "WinDivert açılamadı (hata kodu " + err + ").";
            }
        }

        // --- is parcaciklari

        // Liste ve tablo alan olarak degil parametre olarak geciyor: is parcacigi
        // gec baslarsa o arada Stop() alanlari sifirlamis olabilir.
        private void SpawnWorker(IntPtr handle, bool quic, int seed, int generation,
                                 Hostlist hosts, ContinuationTable continuations)
        {
            StartThread(quic ? "etodpx-quic" : "etodpx-tcp-" + seed,
                        () => WorkerLoop(handle, quic, seed, generation, hosts, continuations));
        }

        private void SpawnContinuationReader(IntPtr handle, ContinuationTable table, int generation)
        {
            StartThread("etodpx-tcp-devam", () => ContinuationLoop(handle, table, generation));
        }

        private void StartThread(string name, ThreadStart body)
        {
            var t = new Thread(body);
            t.IsBackground = true;
            t.Name = name;
            try { t.Priority = ThreadPriority.Highest; } catch { }
            _threads.Add(t);
            try { t.Start(); }
            catch { _threads.Remove(t); throw; }
        }

        // Yalnizca kopya okur, hicbir seyi geri enjekte etmez. Yardimci yol oldugu
        // icin hata motoru durdurmaz; en kotu ihtimalle devam segmentleri eskisi
        // gibi bulunamaz.
        private void ContinuationLoop(IntPtr handle, ContinuationTable table, int generation)
        {
            IntPtr mem = IntPtr.Zero;
            try
            {
                const int cap = 0x10000 + 64;
                mem = Marshal.AllocHGlobal(cap);
                byte* buf = (byte*)mem;
                int consecutiveErrors = 0;

                while (_running && generation == Volatile.Read(ref _generation))
                {
                    uint recvLen = 0;
                    DivertAddress addr = default(DivertAddress);
                    if (!WinDivert.WinDivertRecv(handle, buf, (uint)cap, &recvLen, &addr))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (!_running || err == ErrorNoData) break;
                        if (err == ErrorInsufficientBuffer) continue;
                        if (++consecutiveErrors > 64) break;
                        Thread.Sleep(1);
                        continue;
                    }
                    consecutiveErrors = 0;
                    if (recvLen > 0) table.Record(buf, (int)recvLen);
                }
            }
            catch { }
            finally
            {
                if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem);
            }
        }

        private void WorkerLoop(IntPtr handle, bool quic, int seed, int generation,
                                Hostlist hosts, ContinuationTable continuations)
        {
            DesyncWorker w = null;
            string terminalFault = null;
            try
            {
                w = new DesyncWorker(_cfg, _stats, handle, seed, hosts, continuations);
                lock (_workers) { _workers.Add(w); }
                int consecutiveErrors = 0;

                while (_running && generation == Volatile.Read(ref _generation))
                {
                    uint recvLen = 0;
                    DivertAddress addr = default(DivertAddress);

                    if (!WinDivert.WinDivertRecv(handle, w.RecvBuffer, (uint)w.RecvCapacity, &recvLen, &addr))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (!_running || err == ErrorNoData) break;
                        if (err == ErrorInsufficientBuffer)
                        {
                            // Paket 64 KB tampona sigmadi. Olmamali, ama sessiz gecmiyoruz.
                            _stats.BumpSendError();
                            continue;
                        }
                        if (++consecutiveErrors > 64)
                        {
                            terminalFault = "Paket alımı sürekli başarısız (hata " + err + "). Koruma durduruldu.";
                            break;
                        }
                        Thread.Sleep(1);
                        continue;
                    }
                    consecutiveErrors = 0;
                    if (recvLen == 0) continue;

                    bool handled;
                    try
                    {
                        handled = quic
                            ? w.HandleQuic((int)recvLen, ref addr)
                            : w.HandleTcp((int)recvLen, ref addr);
                    }
                    catch
                    {
                        handled = false;   // ne olursa olsun paketi yutma
                    }

                    if (!handled)
                    {
                        uint sent;
                        if (!WinDivert.WinDivertSend(handle, w.RecvBuffer, recvLen, &sent, &addr))
                            _stats.BumpSendError();       // dokunmadigimiz paket bile kaybolmamali
                        _stats.BumpPassthrough();
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running && generation == Volatile.Read(ref _generation))
                    terminalFault = "Motor hatası: " + ex.Message;
            }
            finally
            {
                if (w != null)
                {
                    lock (_workers) { _workers.Remove(w); }
                    w.Dispose();
                }
                if (terminalFault != null) FailGeneration(generation, terminalFault);
            }
        }

        private void FailGeneration(int generation, string message)
        {
            if (!_running || generation != Volatile.Read(ref _generation)) return;
            if (Interlocked.CompareExchange(ref _faultRaised, 1, 0) != 0) return;

            RaiseFault(message);
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (_lifecycle)
                {
                    if (_running && generation == Volatile.Read(ref _generation)) StopLocked();
                }
            });
        }

        private void RaiseFault(string msg)
        {
            LogFault(msg);
            var h = Fault;
            if (h != null) { try { h(msg); } catch { } }
        }

        // Motor kendi kendine durdugunda pencere tepsideyse geriye iz kalmiyordu.
        private static void LogFault(string msg)
        {
            try
            {
                string dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, "etodpx-fault.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // --- filtreler

        private string PrivateExclusion()
        {
            if (!_cfg.SkipPrivateRanges) return null;
            // Yerel ag, loopback ve link-local hedefleri hic ellemiyoruz:
            // modem arayuzu, NAS, yerel gelistirme sunuculari etkilenmesin.
            string v4 = "((ip.DstAddr < 10.0.0.0 or ip.DstAddr > 10.255.255.255) and "
                      + "(ip.DstAddr < 172.16.0.0 or ip.DstAddr > 172.31.255.255) and "
                      + "(ip.DstAddr < 192.168.0.0 or ip.DstAddr > 192.168.255.255) and "
                      + "(ip.DstAddr < 127.0.0.0 or ip.DstAddr > 127.255.255.255) and "
                      + "(ip.DstAddr < 169.254.0.0 or ip.DstAddr > 169.254.255.255))";

            // fe80::/10 (link-local) ve fc00::/7 (unique local). Surucu bu ifadeyi
            // reddederse aday merdiveni yalniz-IPv4 surumune duser.
            string v6 = "((ipv6.DstAddr < fe80:: or ipv6.DstAddr > febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff) and "
                      + "(ipv6.DstAddr < fc00:: or ipv6.DstAddr > fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff))";

            return "((ipv6 and " + v6 + ") or (ip and " + v4 + "))";
        }

        private static string Join(params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(" and ");
                sb.Append(p);
            }
            return sb.ToString();
        }

        private string TlsClause()
        {
            // 0x16 handshake, 0x03 surum ailesi, payload[5]=0x01 ClientHello.
            // Payload[2] <= 0x04 ve payload[3] <= 0x40 (kayit en fazla 16 KB) ucuz
            // kontroller; rastgele veriyle yanlis eslesmeyi 256 kat azaltiyorlar.
            // Yapisal dogrulama IsClientHello() icinde.
            return "(tcp.DstPort == 443 and tcp.Payload[0] == 0x16 and tcp.Payload[1] == 0x03 "
                 + "and tcp.Payload[2] <= 0x04 and tcp.Payload[3] <= 0x40 and tcp.Payload[5] == 0x01)";
        }

        private string HttpClause()
        {
            // Yalnizca istek satiriyla baslayan paketler: GET/POST/HEAD/PUT/OPTIONS/PATCH/DELETE/CONNECT
            return "(tcp.DstPort == 80 and ("
                 + "tcp.Payload32[0] == 0x47455420 or "
                 + "tcp.Payload32[0] == 0x504f5354 or "
                 + "tcp.Payload32[0] == 0x48454144 or "
                 + "tcp.Payload32[0] == 0x50555420 or "
                 + "tcp.Payload32[0] == 0x4f505449 or "
                 + "tcp.Payload32[0] == 0x50415443 or "
                 + "tcp.Payload32[0] == 0x44454c45 or "
                 + "tcp.Payload32[0] == 0x434f4e4e))";
        }

        private string PortsClause()
        {
            var l = new List<string>();
            if (TlsActive()) l.Add("tcp.DstPort == 443");
            if (HttpActive()) l.Add("tcp.DstPort == 80");
            if (l.Count == 0) return "false";
            return "(" + string.Join(" or ", l.ToArray()) + ")";
        }

        private string PayloadClause()
        {
            var l = new List<string>();
            if (TlsActive()) l.Add(TlsClause());
            if (HttpActive()) l.Add(HttpClause());
            if (l.Count == 0) return "false";
            return "(" + string.Join(" or ", l.ToArray()) + ")";
        }

        private static bool ModeActive(string mode)
        {
            return mode != "off" && mode != "none" && !string.IsNullOrEmpty(mode);
        }

        private bool TlsActive() { return _cfg.EnableTls && ModeActive(_cfg.TlsMode); }
        private bool HttpActive() { return _cfg.EnableHttp && ModeActive(_cfg.HttpMode); }

        // IPv6 sozdizimi reddedilirse bu surum kullanilir.
        private string PrivateExclusionV4Only()
        {
            if (!_cfg.SkipPrivateRanges) return null;
            return "(ipv6 or ("
                 + "(ip.DstAddr < 10.0.0.0 or ip.DstAddr > 10.255.255.255) and "
                 + "(ip.DstAddr < 172.16.0.0 or ip.DstAddr > 172.31.255.255) and "
                 + "(ip.DstAddr < 192.168.0.0 or ip.DstAddr > 192.168.255.255) and "
                 + "(ip.DstAddr < 127.0.0.0 or ip.DstAddr > 127.255.255.255) and "
                 + "(ip.DstAddr < 169.254.0.0 or ip.DstAddr > 169.254.255.255)))";
        }

        // En dardan en genise; ilk derlenebilen kullanilir. Genise dusmek dogrulugu
        // bozmaz, yalnizca kullanici alanina biraz daha fazla paket gelir.
        internal string[] BuildTcpFilterCandidates()
        {
            // !tcp.Syn : TCP Fast Open ile gelen yuklu SYN paketleri hic gelmesin.
            string baseExpr = "outbound and !impostor and !loopback and tcp and !tcp.Syn and tcp.PayloadLength > 0";
            string priv = PrivateExclusion();
            return new[]
            {
                Join(baseExpr, priv, PayloadClause()),
                Join(baseExpr, PrivateExclusionV4Only(), PayloadClause()),
                Join(baseExpr, PayloadClause()),
                Join(baseExpr, priv, PortsClause()),
                Join(baseExpr, PortsClause())
            };
        }

        internal string[] BuildQuicFilterCandidates()
        {
            // Initial uzun baslikli (0xc0..0xcf) ve dolgu yuzunden hep buyuk paket.
            // Kisa baslikli veri paketleri (0x40..0x7f) bu filtreye takilmaz.
            string baseExpr = "outbound and !impostor and !loopback and udp and udp.DstPort == 443 and udp.PayloadLength >= 1200";
            // Ilk bayt tek basina yetmiyor: 0xc0-0xcf rastgele bir baytin %6'si.
            // UDP/443'ten gecen QUIC disi trafik (Steam Datagram Relay - CS2 oradan
            // gecer) yakalanip oyun paketine sahte paket enjekte ediliyordu. Surum
            // alani da dogrulaniyor: v1 (0x00000001) ya da v2.
            string head = "((udp.Payload[0] >= 0xc0 and udp.Payload[0] <= 0xcf and "
                        + "udp.Payload32[1] == 0x00000001) or "
                        + "(udp.Payload[0] >= 0xd0 and udp.Payload[0] <= 0xdf and "
                        + "udp.Payload32[1] == 0x6b3343cf))";
            string priv = PrivateExclusion();
            return new[]
            {
                Join(baseExpr, priv, head),
                Join(baseExpr, PrivateExclusionV4Only(), head),
                Join(baseExpr, head),
                baseExpr
            };
        }

        // Parcali ClientHello'nun son segmenti: 443'e giden, PSH isaretli, tam
        // MSS'ten kisa veri. Toplu yuklemenin tam boy segmentleri (PSH yok) ve
        // butun gelen trafik takilmaz. Tutamak SNIFF kipinde: eslesen paket de
        // bekletilmez, kullanici alanina yalnizca kopyasi gelir.
        internal string[] BuildContinuationFilterCandidates()
        {
            string baseExpr = "outbound and !impostor and !loopback and tcp and !tcp.Syn and tcp.Psh"
                            + " and tcp.DstPort == 443 and tcp.PayloadLength > 0"
                            + " and tcp.PayloadLength <= " + ContinuationTable.MaxPayload;
            return new[]
            {
                Join(baseExpr, PrivateExclusion()),
                Join(baseExpr, PrivateExclusionV4Only()),
                baseExpr
            };
        }

        private static string PickFilter(string[] candidates, DivertLayer layer)
        {
            foreach (var f in candidates)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (FilterCompiles(f, layer)) return f;
            }
            return null;
        }

        internal static bool FilterCompiles(string filter, DivertLayer layer)
        {
            try
            {
                byte* err = null;
                uint pos = 0;
                return WinDivert.WinDivertHelperCompileFilter(filter, layer, null, 0, &err, &pos);
            }
            catch (DllNotFoundException) { return true; }   // DLL yoksa Open zaten anlamli hata verecek
            catch (EntryPointNotFoundException) { return true; }
            catch { return false; }
        }
    }

    internal sealed class EngineException : Exception
    {
        public EngineException(string m) : base(m) { }
    }
}
