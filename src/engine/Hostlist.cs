// Alan adi listesi: yalnizca listedeki adlara dokunulur.
//
// Liste yokken etodpx 443'e giden her ClientHello'yu boluyordu. Engelli
// olmayan bir sunucu icin bunun faydasi yok ama zarari var: DPI kutusu tuhaf
// gordugu paketi dusurunce Windows yeniden gonderiyor ve baglanti TCP zaman
// asimi merdivenini (1+2+4+8+16 sn) tirmaniyor. Oyun icinde 30 saniyelik
// donma, Discord'da yayin acilmamasi buradan geliyordu.
//
// Sicak yolda tahsis yok: dosya acilista okunup tek bir unmanaged bloga
// yaziliyor, eslesme pointer uzerinde yapiliyor.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Etodpx.Engine
{
    internal sealed unsafe class Hostlist : IDisposable
    {
        // Blok yerlesimi: [uzunluk:1][baytlar...] ... [0]
        private readonly IntPtr _mem;
        private readonly byte* _p;

        public int Count { get; private set; }
        public string SourcePath { get; private set; }

        private Hostlist(byte[] blob, int count, string path)
        {
            Count = count;
            SourcePath = path;
            _mem = Marshal.AllocHGlobal(blob.Length);
            Marshal.Copy(blob, 0, _mem, blob.Length);
            _p = (byte*)_mem;
        }

        /// Dosya yoksa null doner - o zaman butun adlara dokunulur (eski davranis).
        public static Hostlist Load(string path, List<string> warnings)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            var names = new List<string>();
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    // "*.site.com" ve ".site.com" ayni sey: son ek eslesmesi zaten var.
                    line = line.TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
                    if (line.Length == 0 || line.Length > 253) continue;
                    if (!names.Contains(line)) names.Add(line);
                }
            }
            catch (Exception ex)
            {
                if (warnings != null) warnings.Add("Alan adı listesi okunamadı: " + ex.Message);
                return null;
            }

            if (names.Count == 0)
            {
                if (warnings != null)
                    warnings.Add("Alan adı listesi boş: " + path + " (bütün adlara dokunulacak)");
                return null;
            }

            var blob = new List<byte>(names.Count * 24);
            foreach (string n in names)
            {
                byte[] b = Encoding.ASCII.GetBytes(n);
                if (b.Length > 255) continue;
                blob.Add((byte)b.Length);
                blob.AddRange(b);
            }
            blob.Add(0);

            return new Hostlist(blob.ToArray(), names.Count, path);
        }

        /// <summary>
        /// Verilen alan adi listede mi. "site.com" girdisi "a.site.com" ile de
        /// eslesir; "kotusite.com" ile eslesmez - son ek nokta sinirinda olmali.
        /// </summary>
        public bool Match(byte* host, int len)
        {
            if (len <= 0) return false;

            byte* e = _p;
            while (*e != 0)
            {
                int n = *e;
                byte* name = e + 1;
                if (n <= len && SuffixEquals(host, len, name, n))
                {
                    // Tam esitlik ya da nokta sinirinda son ek.
                    if (n == len || host[len - n - 1] == (byte)'.') return true;
                }
                e = name + n;
            }
            return false;
        }

        private static bool SuffixEquals(byte* host, int hostLen, byte* name, int nameLen)
        {
            byte* h = host + (hostLen - nameLen);
            for (int i = 0; i < nameLen; i++)
            {
                byte c = h[i];
                if (c >= (byte)'A' && c <= (byte)'Z') c += 32;
                if (c != name[i]) return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (_mem != IntPtr.Zero) Marshal.FreeHGlobal(_mem);
        }
    }
}
