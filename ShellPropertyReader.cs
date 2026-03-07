using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DesktopPlus
{
    internal static class ShellPropertyReader
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid FmtId;
            public uint PropertyId;

            public PropertyKey(Guid fmtId, uint propertyId)
            {
                FmtId = fmtId;
                PropertyId = propertyId;
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)]
            public ushort VariantType;

            [FieldOffset(8)]
            public IntPtr PointerValue;
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            uint GetCount(out uint propertyCount);
            uint GetAt(uint propertyIndex, out PropertyKey key);
            uint GetValue(ref PropertyKey key, out PropVariant value);
            uint SetValue(ref PropertyKey key, ref PropVariant value);
            uint Commit();
        }

        [Flags]
        private enum GetPropertyStoreFlags : uint
        {
            Default = 0,
            BestEffort = 0x00000040
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHGetPropertyStoreFromParsingName(
            string path,
            IntPtr zeroWorksAsBindContext,
            GetPropertyStoreFlags flags,
            ref Guid propertyStoreGuid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

        [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
        private static extern int PropVariantToStringAlloc(
            ref PropVariant propVariant,
            out IntPtr resultStringPointer);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant propVariant);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pointer);

        private static readonly PropertyKey PropertyTitle =
            new PropertyKey(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
        private static readonly PropertyKey PropertyAuthors =
            new PropertyKey(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 4);
        private static readonly PropertyKey PropertyKeywords =
            new PropertyKey(new Guid("D5CDD505-2E9C-101B-9397-08002B2CF9AE"), 5);
        private static readonly PropertyKey PropertyCategories =
            new PropertyKey(new Guid("D5CDD505-2E9C-101B-9397-08002B2CF9AE"), 2);

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> CacheOrder = new Queue<string>();
        private const int CacheLimit = 4096;

        public static string GetValue(string path, string metadataKey)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(metadataKey))
            {
                return string.Empty;
            }

            PropertyKey? propertyKey = GetPropertyKey(metadataKey);
            if (propertyKey == null)
            {
                return string.Empty;
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(path);
            }
            catch
            {
                normalizedPath = path;
            }

            string cacheKey = $"{normalizedPath}|{metadataKey}";
            lock (CacheLock)
            {
                if (Cache.TryGetValue(cacheKey, out string? cachedValue))
                {
                    return cachedValue;
                }
            }

            string value = ReadPropertyValue(normalizedPath, propertyKey.Value);

            lock (CacheLock)
            {
                Cache[cacheKey] = value;
                CacheOrder.Enqueue(cacheKey);
                while (CacheOrder.Count > CacheLimit)
                {
                    string staleKey = CacheOrder.Dequeue();
                    Cache.Remove(staleKey);
                }
            }

            return value;
        }

        public static void InvalidatePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(path);
            }
            catch
            {
                normalizedPath = path;
            }

            lock (CacheLock)
            {
                foreach (string key in Cache.Keys
                    .Where(key => key.StartsWith(normalizedPath + "|", StringComparison.OrdinalIgnoreCase))
                    .ToList())
                {
                    Cache.Remove(key);
                }
            }
        }

        private static PropertyKey? GetPropertyKey(string metadataKey)
        {
            if (string.Equals(metadataKey, DesktopPanel.MetadataTitle, StringComparison.OrdinalIgnoreCase))
            {
                return PropertyTitle;
            }

            if (string.Equals(metadataKey, DesktopPanel.MetadataAuthors, StringComparison.OrdinalIgnoreCase))
            {
                return PropertyAuthors;
            }

            if (string.Equals(metadataKey, DesktopPanel.MetadataCategories, StringComparison.OrdinalIgnoreCase))
            {
                return PropertyCategories;
            }

            if (string.Equals(metadataKey, DesktopPanel.MetadataTags, StringComparison.OrdinalIgnoreCase))
            {
                return PropertyKeywords;
            }

            return null;
        }

        private static string ReadPropertyValue(string path, PropertyKey propertyKey)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return string.Empty;
            }

            IPropertyStore? propertyStore = null;
            PropVariant propertyValue = default;

            try
            {
                Guid propertyStoreGuid = typeof(IPropertyStore).GUID;
                int hr = SHGetPropertyStoreFromParsingName(
                    path,
                    IntPtr.Zero,
                    GetPropertyStoreFlags.BestEffort,
                    ref propertyStoreGuid,
                    out propertyStore);
                if (hr < 0 || propertyStore == null)
                {
                    return string.Empty;
                }

                hr = unchecked((int)propertyStore.GetValue(ref propertyKey, out propertyValue));
                if (hr < 0)
                {
                    return string.Empty;
                }

                hr = PropVariantToStringAlloc(ref propertyValue, out IntPtr resultPointer);
                if (hr < 0 || resultPointer == IntPtr.Zero)
                {
                    return string.Empty;
                }

                try
                {
                    string value = Marshal.PtrToStringUni(resultPointer) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return string.Empty;
                    }

                    return value
                        .Replace("; ", ", ", StringComparison.Ordinal)
                        .Replace(";", ", ", StringComparison.Ordinal)
                        .Trim();
                }
                finally
                {
                    CoTaskMemFree(resultPointer);
                }
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                PropVariantClear(ref propertyValue);
                if (propertyStore != null)
                {
                    Marshal.ReleaseComObject(propertyStore);
                }
            }
        }
    }
}
