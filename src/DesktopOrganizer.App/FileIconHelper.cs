using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class FileIconHelper
{
    public static ImageSource GetFileIcon(string filePath)
    {
        // Получение иконки через Shell API
        var shfi = new SHFILEINFO();
        IntPtr hIcon = SHGetFileInfo(filePath, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_ICON | SHGFI_SMALLICON);

        if (hIcon != IntPtr.Zero)
        {
            try
            {
                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                // Удаляем иконку из памяти, освобождаем ресурсы
                DestroyIcon(shfi.hIcon);

                return imageSource;
            }
            catch
            {
                DestroyIcon(shfi.hIcon); // Освобождение ресурсов при ошибке
            }
        }

        return null; // Возврат null, если иконка недоступна
    }

    // Импорты для работы с Shell API
    [DllImport("Shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("User32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Константы для Shell API
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;

    private struct SHFILEINFO
    {
        public IntPtr hIcon; // Handle к иконке
        public int iIcon; // Индекс иконки
        public uint dwAttributes; // Атрибуты файла
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName; // Полное имя файла
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName; // Тип файла
    }
}
