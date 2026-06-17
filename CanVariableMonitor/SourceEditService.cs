using System.Text;

namespace CanVariableMonitor;

internal sealed class SourceFileBuffer
{
    public required string FilePath { get; init; }
    public required string Text { get; init; }
    public required Encoding Encoding { get; init; }
    public required string NewLine { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public required long Length { get; init; }
}

internal sealed class SourceEditSaveResult
{
    public bool Success { get; init; }
    public bool Conflict { get; init; }
    public string Message { get; init; } = "";
    public SourceFileBuffer? Buffer { get; init; }
}

internal sealed class SourceEditSession
{
    private SourceEditSession(SourceFileBuffer buffer)
    {
        Buffer = buffer;
    }

    public SourceFileBuffer Buffer { get; private set; }
    public bool Dirty { get; private set; }
    public bool HasBackup { get; private set; }
    public bool HasConflict { get; private set; }
    public DateTime LastSavedUtc { get; private set; }

    public string FilePath => Buffer.FilePath;

    public static SourceEditSession Load(string filePath)
    {
        return new SourceEditSession(SourceEditService.LoadFile(filePath));
    }

    public void MarkDirty(string currentText)
    {
        Dirty = !currentText.Equals(Buffer.Text, StringComparison.Ordinal);
        if (Dirty)
        {
            HasConflict = false;
        }
    }

    public void Refresh(SourceFileBuffer buffer)
    {
        Buffer = buffer;
        Dirty = false;
        HasConflict = false;
    }

    public SourceEditSaveResult Save(string currentText)
    {
        if (!Dirty && currentText.Equals(Buffer.Text, StringComparison.Ordinal))
        {
            return new SourceEditSaveResult
            {
                Success = true,
                Message = "源码未修改。",
                Buffer = Buffer
            };
        }

        if (SourceEditService.HasExternalChange(Buffer))
        {
            HasConflict = true;
            return new SourceEditSaveResult
            {
                Success = false,
                Conflict = true,
                Message = "磁盘源码已被外部程序修改，自动保存已停止。请重新载入或手工合并。"
            };
        }

        if (!HasBackup)
        {
            SourceEditService.CreateSingleBackup(Buffer.FilePath);
            HasBackup = true;
        }

        SourceEditService.WriteFile(Buffer.FilePath, currentText, Buffer.Encoding, Buffer.NewLine);
        Buffer = SourceEditService.LoadFile(Buffer.FilePath);
        Dirty = false;
        HasConflict = false;
        LastSavedUtc = DateTime.UtcNow;
        return new SourceEditSaveResult
        {
            Success = true,
            Message = "源码已保存：" + Buffer.FilePath,
            Buffer = Buffer
        };
    }
}

internal static class SourceEditService
{
    private static readonly Encoding Utf8NoBomStrict = new UTF8Encoding(false, true);

    static SourceEditService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static SourceFileBuffer LoadFile(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        Encoding encoding = DetectEncoding(bytes);
        string text = encoding.GetString(bytes);
        FileInfo info = new FileInfo(filePath);
        return new SourceFileBuffer
        {
            FilePath = filePath,
            Text = text,
            Encoding = encoding,
            NewLine = DetectNewLine(text),
            LastWriteUtc = info.LastWriteTimeUtc,
            Length = info.Length
        };
    }

    public static bool HasExternalChange(SourceFileBuffer buffer)
    {
        if (!File.Exists(buffer.FilePath))
        {
            return true;
        }

        FileInfo info = new FileInfo(buffer.FilePath);
        return info.LastWriteTimeUtc != buffer.LastWriteUtc || info.Length != buffer.Length;
    }

    public static void WriteFile(string filePath, string text, Encoding encoding, string newLine)
    {
        string normalized = NormalizeNewLines(text, newLine);
        File.WriteAllBytes(filePath, encoding.GetBytes(normalized));
    }

    public static string CreateSingleBackup(string filePath)
    {
        string backupPath = filePath + ".bak";
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        File.Copy(filePath, backupPath, overwrite: true);
        return backupPath;
    }

    public static int RunSelfTest(TextWriter output)
    {
        string root = Path.Combine(Path.GetTempPath(), "CanVariableMonitor_SourceEditSelfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string utf8Path = Path.Combine(root, "utf8.c");
            string gbPath = Path.Combine(root, "gb18030.c");
            File.WriteAllText(utf8Path, "int value = 1;\r\n// 中文注释\r\n", new UTF8Encoding(true));
            File.WriteAllBytes(gbPath, Encoding.GetEncoding("GB18030").GetBytes("int value = 1;\r\n// 中文注释\r\n"));

            SourceFileBuffer utf8 = LoadFile(utf8Path);
            SourceEditSession session = SourceEditSession.Load(utf8Path);
            session.MarkDirty(utf8.Text.Replace("1", "2", StringComparison.Ordinal));
            SourceEditSaveResult save = session.Save(utf8.Text.Replace("1", "2", StringComparison.Ordinal));
            bool backupOk = File.Exists(utf8Path + ".bak");
            bool utf8Ok = save.Success && backupOk && LoadFile(utf8Path).Text.Contains("中文注释", StringComparison.Ordinal);

            SourceFileBuffer gb = LoadFile(gbPath);
            bool gbOk = gb.Encoding.WebName.Contains("gb18030", StringComparison.OrdinalIgnoreCase) &&
                gb.Text.Contains("中文注释", StringComparison.Ordinal);

            SourceEditSession conflict = SourceEditSession.Load(gbPath);
            File.AppendAllText(gbPath, "// external\r\n", Encoding.GetEncoding("GB18030"));
            conflict.MarkDirty(conflict.Buffer.Text.Replace("1", "3", StringComparison.Ordinal));
            SourceEditSaveResult conflictSave = conflict.Save(conflict.Buffer.Text.Replace("1", "3", StringComparison.Ordinal));
            bool conflictOk = conflictSave.Conflict && !LoadFile(gbPath).Text.Contains("3", StringComparison.Ordinal);

            bool symbolOk = SourceSymbolIndex.RunSelfTest(output) == 0;
            bool buildOk = KeilBuildService.RunSelfTest(output) == 0;

            bool ok = utf8Ok && gbOk && conflictOk && symbolOk && buildOk;
            output.WriteLine(ok ? "SourceEditSelfTest: PASS" : "SourceEditSelfTest: FAIL");
            output.WriteLine($"  utf8={utf8Ok}, gb18030={gbOk}, backup={backupOk}, conflict={conflictOk}, symbol={symbolOk}, buildLog={buildOk}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            output.WriteLine("SourceEditSelfTest: FAIL");
            output.WriteLine(ex);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(true, true);
        }
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode;
            }
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode;
            }
        }

        try
        {
            _ = Utf8NoBomStrict.GetString(bytes);
            return new UTF8Encoding(false, true);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030");
        }
    }

    private static string DetectNewLine(string text)
    {
        int crlf = CountOccurrences(text, "\r\n");
        int lf = CountOccurrences(text.Replace("\r\n", "", StringComparison.Ordinal), "\n");
        int cr = CountOccurrences(text.Replace("\r\n", "", StringComparison.Ordinal), "\r");
        if (crlf >= lf && crlf >= cr && crlf > 0)
        {
            return "\r\n";
        }
        if (lf >= cr && lf > 0)
        {
            return "\n";
        }
        if (cr > 0)
        {
            return "\r";
        }
        return Environment.NewLine;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string NormalizeNewLines(string text, string newLine)
    {
        string value = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return newLine == "\n" ? value : value.Replace("\n", newLine, StringComparison.Ordinal);
    }
}
