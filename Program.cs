using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace HardLinkDuplicates
{
    class Program
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern SafeFileHandle CreateFile(
            string lpFileName,
            [MarshalAs(UnmanagedType.U4)] FileAccess dwDesiredAccess,
            [MarshalAs(UnmanagedType.U4)] FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            [MarshalAs(UnmanagedType.U4)] FileMode dwCreationDisposition,
            [MarshalAs(UnmanagedType.U4)] FileAttributes dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            private readonly uint FileAttributes;
            private readonly FILETIME CreationTime;
            private readonly FILETIME LastAccessTime;
            private readonly FILETIME LastWriteTime;
            public readonly uint VolumeSerialNumber;
            private readonly uint FileSizeHigh;
            private readonly uint FileSizeLow;
            private readonly uint NumberOfLinks;
            public readonly uint FileIndexHigh;
            public readonly uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile,
                                                              out ByHandleFileInformation lpFileInformation);

        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool CreateHardLink(
            string lpFileName,
            string lpExistingFileName,
            IntPtr lpSecurityAttributes
            );

        private static string GetVolumeFileIndexString(string filePath)
        {
            var handle = CreateFile(filePath, FileAccess.Read, FileShare.Read, IntPtr.Zero, FileMode.Open,
                                    FileAttributes.Normal, IntPtr.Zero);
            if (handle.IsInvalid)
                return null;

            ByHandleFileInformation fileInfo;
            if (GetFileInformationByHandle(handle, out fileInfo))
            {
                handle.Close();
                return $"{fileInfo.VolumeSerialNumber:X8}{fileInfo.FileIndexHigh:X8}{fileInfo.FileIndexLow:X8}";
            }
            handle.Close();
            return null;
        }

        static void Main(string[] args)
        {
            var currentDir = Directory.GetCurrentDirectory();
            var mask = args.Any() ? args[0] : "*.*";
            Console.WriteLine("Scanning '{0}' for '{1}'", currentDir, mask);
            var filesEnum = Directory.EnumerateFiles(currentDir, mask, SearchOption.AllDirectories);
            var filesList = new List<string>(1024*1024);
            foreach (var file in filesEnum)
            {
                if ((filesList.Count%100) == 0)
                    Console.Write("Found {0} files..\r", filesList.Count);
                filesList.Add(file);
            }
            Console.WriteLine("Found {0} files..", filesList.Count);

            var perSizeHashDictionary = new Dictionary<long, Dictionary<string, List<string>>>();
            var firstFileForSizeDictionary = new Dictionary<long, string>();
            var checkedVfis = new HashSet<string>();
            var n = 0;
            var hardlinks = 0;
            foreach (var file in filesList)
            {
                if (n++%50 == 0)
                {
                    Console.WriteLine("");
                    var percent = 100*n/filesList.Count;
                    Console.Write("{0}% ({1}/{2})\r", percent, n, filesList.Count);
                }

                var size = new FileInfo(file).Length;
                if (size < 1024)
                    continue;

                var vfis = GetVolumeFileIndexString(file);
                if (vfis != null)
                {
                    if (!checkedVfis.Add(vfis))
                    {
                        hardlinks++;
                        continue;
                    }
                }

                string firstFileForSize;
                if (!firstFileForSizeDictionary.TryGetValue(size, out firstFileForSize))
                {
                    firstFileForSizeDictionary[size] = file;
                    continue;
                }

                Dictionary<string, List<string>> dic;
                if (!perSizeHashDictionary.TryGetValue(size, out dic))
                {
                    dic = new Dictionary<string, List<string>>();
                    perSizeHashDictionary[size] = dic;
                    AddFileToListPerHash(dic, firstFileForSize);
                }

                AddFileToListPerHash(dic, file);
            }
            Console.WriteLine("{0}% ({1}/{2})", 100, n, filesList.Count);

            var allSets = perSizeHashDictionary.Values.SelectMany(dic => dic.Values).ToArray();
            var dupeSets = allSets.Where(list => list.Count > 1).ToArray();
            var uniqueFiles = n - dupeSets.Length;
            var totalInDupeSets = dupeSets.Sum(set => set.Count);

            Console.WriteLine("Found {0} unique files, and {1} sets of duplicates with total {2} files", uniqueFiles, dupeSets.Length, totalInDupeSets);
            var bytesToSave = dupeSets.SelectMany(list => list.Skip(1)).Sum(fn => new FileInfo(fn).Length);
            Console.WriteLine("Found {0} pre-existing hardlinks", hardlinks);
            Console.WriteLine("Total bytes to save: {0} or {1}kb or {2}Mb or {3}Gb or {4}Tb", bytesToSave, bytesToSave / 1024L, bytesToSave / (1024L * 1024L),
                              bytesToSave/(1024L*1024L*1024L), bytesToSave/(1024L*1024L*1024L*1024L));
            Console.WriteLine("Press 'y' to continue and create the hard links");
            if (Console.ReadKey().KeyChar=='y')
                foreach (var dupeSet in dupeSets)
                    OptimizeDupeSet(dupeSet);
        }

        private static void AddFileToListPerHash(Dictionary<string, List<string>> dic, string file)
        {
            try
            {
                var hash = GetFullHash(file);
                List<string> filesPerHash;
                if (!dic.TryGetValue(hash, out filesPerHash))
                {
                    filesPerHash = new List<string>();
                    dic[hash] = filesPerHash;
                }
                filesPerHash.Add(file);
            }
            catch (Exception e)
            {
                Console.WriteLine("Couldn't read {0}, ignored file. [{1}]", file, e.Message);
            }
        }

        private static void OptimizeDupeSet(List<string> dupeSet)
        {
            var original = dupeSet.First();
            foreach (var dupe in dupeSet.Skip(1))
                DeleteAndCreateHardLink(original, dupe);
        }

        private static void DeleteAndCreateHardLink(string original, string dupe)
        {
            var tempName = dupe + ".bak";
            File.Move(dupe, tempName);
            if (!CreateHardLink(dupe, original, IntPtr.Zero))
            {
                Console.WriteLine("Couldn't hardlink {0} to {1}; restoring backup", original, dupe);
                File.Move(tempName, dupe);
            }
            else
            {
                Console.WriteLine("Hardlinked {0} to {1}; deleting backup", original, dupe);
                File.Delete(tempName);
            }
        }

        private static readonly byte[] Buffer = new byte[1024*1024];

        // ReSharper disable once UnusedMember.Local
        private static string Get1MHash(string file)
        {
            using (var strm = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var read = strm.Read(Buffer, 0, 1024*1024);
                return CalculateMd5Hash(Buffer, read) + new FileInfo(file).Length.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string GetFullHash(string file)
        {
            using (var strm = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return CalculateMd5Hash(strm);
            }
        }

        public static string CalculateMd5Hash(Stream stream)
        {
            // step 1, calculate MD5 hash from input
            var md5 = MD5.Create();
            var hash = md5.ComputeHash(stream);

            // step 2, convert byte array to hex string
            var sb = new StringBuilder();
            foreach (var t in hash)
            {
                sb.Append(t.ToString("X2"));
            }
            return sb.ToString();
        }

        public static string CalculateMd5Hash(byte[] inputBytes, int count)
        {
            // step 1, calculate MD5 hash from input
            var md5 = MD5.Create();
            var hash = md5.ComputeHash(inputBytes, 0, count);

            // step 2, convert byte array to hex string
            var sb = new StringBuilder();
            foreach (byte t in hash)
            {
                sb.Append(t.ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
