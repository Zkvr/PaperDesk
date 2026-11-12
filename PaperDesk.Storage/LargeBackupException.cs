using PaperDesk.Core;

namespace PaperDesk.Storage;

public sealed class LargeBackupException(string fileName)
    : IOException(L.F("Копия «{0}» превышает {1} МиБ.", fileName, LimitMiB))
{
    public const int LimitMiB = 256;
}
