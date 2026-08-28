// etodpx - derleme kimligi.
//
// Depoda derlenmis exe yok; kullanici indirdigi dosyanin hangi surum
// oldugunu ancak Ozellikler > Ayrintilar penceresinden gorebilir. Bu
// nitelikler olmadan orasi bos gorunur.
//
// Surum degistirirken src\app.manifest icindeki assemblyIdentity da
// ayni degere cekilmeli.
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("etodpx")]
[assembly: AssemblyDescription("Windows icin DPI atlatma araci")]
[assembly: AssemblyProduct("etodpx")]
[assembly: AssemblyCopyright("Copyright (c) 2026 valenciaennerman-cmd - MIT")]
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]
[assembly: ComVisible(false)]
