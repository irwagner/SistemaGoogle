using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Central.Domain;

namespace Central.Operations;

public sealed class WindowsVault
{
    private readonly string root;
    public WindowsVault(Store store)
    {
        var scope = Store.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(store.Root).ToUpperInvariant()));
        root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CentralProducao", "cofre", scope);
    }

    public void Put<T>(string key, T value)
    {
        var path = VaultPath(key);
        Directory.CreateDirectory(root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var protectedBytes = Transform(bytes, encrypt: true);
            var temporary = path + "." + Store.Id();
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public T? Get<T>(string key)
    {
        var path = VaultPath(key);
        if (!File.Exists(path)) return default;
        var bytes = Transform(File.ReadAllBytes(path), encrypt: false);
        try { return JsonSerializer.Deserialize<T>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Remove(string key) => File.Delete(VaultPath(key));

    private string VaultPath(string key)
    {
        var path = Path.Combine(root, Store.Hash(Encoding.UTF8.GetBytes(key)) + ".dpapi");
        Store.RejectLinks(path);
        return path;
    }

    private static byte[] Transform(byte[] data, bool encrypt)
    {
        var input = new DataBlob { Count = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = encrypt
                ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new DomainError("O cofre deste usuário Windows está indisponível ("
                + new Win32Exception(Marshal.GetLastWin32Error()).NativeErrorCode + ").");
            var bytes = new byte[output.Count];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.Copy(new byte[data.Length], 0, input.Data, input.Count);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                if (!encrypt) Marshal.Copy(new byte[output.Count], 0, output.Data, output.Count);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Count; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob result);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob result);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
