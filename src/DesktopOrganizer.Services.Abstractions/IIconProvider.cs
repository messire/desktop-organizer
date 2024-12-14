using System.Windows.Media;

namespace DesktopOrganizer.Services.Abstractions;

public interface IIconProvider
{
    ImageSource GetIcon(string filePath);
}
