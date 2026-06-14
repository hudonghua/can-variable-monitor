namespace CanVariableMonitor;

internal static class KeilToolLocator
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CanVariableMonitor",
        "keil_tools.txt");

    public static string? FindUv4()
    {
        return FindTool("UV4", "UV4.exe", BuildUv4Candidates);
    }

    public static string? FindFromElf()
    {
        return FindTool("FROMELF", "fromelf.exe", BuildFromElfCandidates);
    }

    private static string? FindTool(string cacheKey, string fileName, Func<IEnumerable<string>> candidateFactory)
    {
        string? cached = ReadCachedPath(cacheKey);
        if (IsExistingFile(cached, fileName))
        {
            return cached;
        }

        foreach (string candidate in candidateFactory().Concat(BuildPathCandidates(fileName)).Concat(SearchLikelyInstallFolders(fileName)))
        {
            if (IsExistingFile(candidate, fileName))
            {
                WriteCachedPath(cacheKey, candidate);
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildUv4Candidates()
    {
        string[] relative =
        {
            @"Keil_v5\UV4\UV4.exe",
            @"Keil\UV4\UV4.exe",
            @"MDK5\UV4\UV4.exe",
            @"Keil_MDK\UV4\UV4.exe",
            @"ARM\Keil_v5\UV4\UV4.exe",
            @"Program Files\Keil_v5\UV4\UV4.exe",
            @"Program Files (x86)\Keil_v5\UV4\UV4.exe"
        };

        foreach (string candidate in BuildDriveCandidates(relative))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> BuildFromElfCandidates()
    {
        string? uv4 = FindUv4();
        if (!string.IsNullOrWhiteSpace(uv4))
        {
            string? keilRoot = Path.GetDirectoryName(Path.GetDirectoryName(uv4)!);
            if (!string.IsNullOrWhiteSpace(keilRoot))
            {
                yield return Path.Combine(keilRoot, "ARM", "ARMCC", "bin", "fromelf.exe");
                yield return Path.Combine(keilRoot, "ARM", "ARMCLANG", "bin", "fromelf.exe");
                yield return Path.Combine(keilRoot, "ARM", "BIN40", "fromelf.exe");
            }
        }

        string[] relative =
        {
            @"Keil_v5\ARM\ARMCC\bin\fromelf.exe",
            @"Keil_v5\ARM\ARMCLANG\bin\fromelf.exe",
            @"Keil_v5\ARM\BIN40\fromelf.exe",
            @"Keil\ARM\ARMCC\bin\fromelf.exe",
            @"Keil\ARM\ARMCLANG\bin\fromelf.exe",
            @"Keil\ARM\BIN40\fromelf.exe",
            @"MDK5\ARM\ARMCC\bin\fromelf.exe",
            @"MDK5\ARM\ARMCLANG\bin\fromelf.exe"
        };

        foreach (string candidate in BuildDriveCandidates(relative))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> BuildDriveCandidates(IEnumerable<string> relatives)
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            foreach (string relative in relatives)
            {
                yield return Path.Combine(drive.RootDirectory.FullName, relative);
            }
        }
    }

    private static IEnumerable<string> BuildPathCandidates(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            yield break;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            yield return Path.Combine(dir.Trim(), fileName);
        }
    }

    private static IEnumerable<string> SearchLikelyInstallFolders(string fileName)
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            IEnumerable<string> roots;
            try
            {
                roots = Directory.EnumerateDirectories(drive.RootDirectory.FullName)
                    .Where(IsLikelyKeilFolder)
                    .Concat(CommonProgramFolders(drive.RootDirectory.FullName));
            }
            catch
            {
                continue;
            }

            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string candidate in SearchFolder(root, fileName))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> CommonProgramFolders(string driveRoot)
    {
        string[] programRoots =
        {
            Path.Combine(driveRoot, "Program Files"),
            Path.Combine(driveRoot, "Program Files (x86)")
        };

        foreach (string programRoot in programRoots)
        {
            if (!Directory.Exists(programRoot))
            {
                continue;
            }

            IEnumerable<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(programRoot).Where(IsLikelyKeilFolder).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (string folder in folders)
            {
                yield return folder;
            }
        }
    }

    private static bool IsLikelyKeilFolder(string path)
    {
        string name = Path.GetFileName(path);
        return name.Contains("Keil", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MDK", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ARM", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SearchFolder(string root, string fileName)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        string direct = Path.Combine(root, fileName);
        if (File.Exists(direct))
        {
            yield return direct;
        }

        IEnumerable<string> matches;
        try
        {
            matches = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).Take(1).ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (string match in matches)
        {
            yield return match;
        }
    }

    private static bool IsExistingFile(string? path, string fileName)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(path);
    }

    private static string? ReadCachedPath(string key)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            foreach (string line in File.ReadAllLines(CachePath))
            {
                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                if (line[..split].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(split + 1)..].Trim();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static void WriteCachedPath(string key, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(CachePath))
            {
                foreach (string line in File.ReadAllLines(CachePath))
                {
                    int split = line.IndexOf('=');
                    if (split > 0)
                    {
                        values[line[..split]] = line[(split + 1)..];
                    }
                }
            }

            values[key] = path;
            File.WriteAllLines(CachePath, values.Select(pair => pair.Key + "=" + pair.Value));
        }
        catch
        {
        }
    }
}
