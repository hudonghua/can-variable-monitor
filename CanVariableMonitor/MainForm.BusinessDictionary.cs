using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CanVariableMonitor;

public sealed partial class MainForm
{
	private ProjectBusinessDictionary _businessDictionary = ProjectBusinessDictionary.Empty;

	private string _businessDictionaryDirectory = "";

	private int _businessDictionaryLoadSerial;

	private void QueueBusinessDictionaryRefresh(string directory, bool force = false)
	{
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return;
		}

		string root;
		try
		{
			root = Path.GetFullPath(directory);
		}
		catch
		{
			return;
		}

		if (!force &&
			_businessDictionaryDirectory.Equals(root, StringComparison.OrdinalIgnoreCase) &&
			_businessDictionary.TermCount > 0)
		{
			return;
		}

		int serial = Interlocked.Increment(ref _businessDictionaryLoadSerial);
		Task.Run(() => ProjectBusinessDictionary.Load(root)).ContinueWith(task =>
		{
			if (IsDisposed)
			{
				return;
			}

			try
			{
				BeginInvoke((Action)(() =>
				{
					if (serial != _businessDictionaryLoadSerial ||
						!_workDirectory.Equals(root, StringComparison.OrdinalIgnoreCase))
					{
						return;
					}

					if (task.Status == TaskStatus.RanToCompletion)
					{
						_businessDictionary = task.Result;
						_businessDictionaryDirectory = root;
						_lastFunctionAnalysisSignature = "";
						RenderFunctionAnalysis(force: true);
						Log($"业务资料：识别 {_businessDictionary.TermCount} 条说明，来自 {_businessDictionary.SourceFileCount} 个资料/源码文件。");
					}
					else if (task.Exception != null)
					{
						Log("业务资料读取失败：" + task.Exception.GetBaseException().Message);
					}
				}));
			}
			catch
			{
			}
		}, TaskScheduler.Default);
	}

	private string DescribeBusinessIdentifier(string identifier)
	{
		return TryDescribeBusinessIdentifier(identifier, out string description) ? description : "";
	}

	private bool TryDescribeBusinessIdentifier(string identifier, out string description)
	{
		description = "";
		if (string.IsNullOrWhiteSpace(identifier))
		{
			return false;
		}

		string resolvedName = ResolveFunctionAnalysisSymbolName(identifier);
		if (_businessDictionary.TryDescribe(identifier, out description) ||
			(resolvedName.Length > 0 &&
				!resolvedName.Equals(identifier, StringComparison.OrdinalIgnoreCase) &&
				_businessDictionary.TryDescribe(resolvedName, out description)))
		{
			return true;
		}

		return TryInferIdentifierDescription(resolvedName.Length > 0 ? resolvedName : identifier, out description);
	}

	private string EnrichFunctionAnalysisDetail(string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
		{
			return detail;
		}

		int annotated = 0;
		string enriched = Regex.Replace(detail, @"\b[A-Za-z_][A-Za-z0-9_]*\b", match =>
		{
			if (annotated >= 2)
			{
				return match.Value;
			}

			string token = match.Value;
			if (IsCKeyword(token) || IsMonitorInternalFunctionName(token))
			{
				return token;
			}

			int nextIndex = match.Index + match.Length;
			if (nextIndex < detail.Length && detail[nextIndex] == '(')
			{
				return token;
			}

			if (!TryDescribeBusinessIdentifier(token, out string description))
			{
				return token;
			}

			description = ShortenBusinessDescription(description, 16);
			if (description.Length == 0 || detail.IndexOf(token + "(", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return token;
			}

			annotated++;
			return token + "(" + description + ")";
		});

		return ShortenAnalysisText(enriched, 72);
	}

	private string FormatFunctionSignalDisplay(FunctionSignal signal, int maxDescriptionLength)
	{
		string description = signal.Description;
		if (string.IsNullOrWhiteSpace(description))
		{
			description = DescribeBusinessIdentifier(signal.Name);
		}

		if (string.IsNullOrWhiteSpace(description))
		{
			return signal.Name;
		}

		return signal.Name + "(" + ShortenBusinessDescription(description, maxDescriptionLength) + ")";
	}

	private static string ShortenBusinessDescription(string text, int maxLength)
	{
		text = ProjectBusinessDictionary.CleanMeaning(text);
		if (text.Length <= maxLength)
		{
			return text;
		}

		int stop = text.IndexOfAny(new[] { '，', '。', '；', ';', ',', '.', '、', '|', '/', '\\' });
		if (stop > 1 && stop <= maxLength)
		{
			return text.Substring(0, stop).Trim();
		}

		return text.Substring(0, Math.Max(1, maxLength - 1)).Trim() + "…";
	}

	private static bool TryInferIdentifierDescription(string identifier, out string description)
	{
		description = "";
		string name = NormalizeFunctionAnalysisIdentifier(identifier);
		if (name.Length == 0)
		{
			return false;
		}

		string upper = name.ToUpperInvariant();
		if (Regex.IsMatch(name, @"\bAI[_A-Za-z]*Pin\d+\b", RegexOptions.IgnoreCase))
		{
			description = "模拟量输入通道";
			return true;
		}
		if (upper.StartsWith("AI_", StringComparison.Ordinal) || upper.Contains("_AI", StringComparison.Ordinal))
		{
			description = "模拟量输入";
			return true;
		}
		if (upper.StartsWith("AO_", StringComparison.Ordinal) || upper.Contains("_AO", StringComparison.Ordinal))
		{
			description = "模拟量输出";
			return true;
		}
		if (upper.EndsWith("_DO", StringComparison.Ordinal) || upper.StartsWith("DO_", StringComparison.Ordinal))
		{
			description = "数字输出点";
			return true;
		}
		if (upper.EndsWith("_DI", StringComparison.Ordinal) || upper.StartsWith("DI_", StringComparison.Ordinal))
		{
			description = "数字输入点";
			return true;
		}
		if (upper.Contains("CAN", StringComparison.Ordinal))
		{
			description = "CAN业务数据";
			return true;
		}
		if (upper.Contains("LCD", StringComparison.Ordinal) || upper.Contains("DISP", StringComparison.Ordinal))
		{
			description = "屏幕显示数据";
			return true;
		}
		return false;
	}

	private sealed class ProjectBusinessDictionary
	{
		public static readonly ProjectBusinessDictionary Empty = new(
			new Dictionary<string, BusinessTerm>(StringComparer.OrdinalIgnoreCase),
			0,
			"empty");

		private readonly Dictionary<string, BusinessTerm> _terms;

		private ProjectBusinessDictionary(Dictionary<string, BusinessTerm> terms, int sourceFileCount, string signature)
		{
			_terms = terms;
			SourceFileCount = sourceFileCount;
			Signature = signature;
		}

		public int TermCount => _terms.Count;

		public int SourceFileCount { get; }

		public string Signature { get; }

		public static ProjectBusinessDictionary Load(string workDirectory)
		{
			var builder = new ProjectBusinessDictionaryBuilder(workDirectory);
			builder.ScanSourceComments();
			builder.ScanDocuments();
			return builder.Build();
		}

		public bool TryDescribe(string identifier, out string description)
		{
			description = "";
			foreach (string key in BuildLookupKeys(identifier))
			{
				if (_terms.TryGetValue(key, out BusinessTerm? term))
				{
					description = term.Description;
					return description.Length > 0;
				}
			}

			string normalized = NormalizeDictionaryKey(identifier);
			if (normalized.Length >= 5)
			{
				List<BusinessTerm> suffixMatches = _terms.Values
					.Where(term =>
						term.Key.Length >= 4 &&
						(normalized.EndsWith(term.Key, StringComparison.OrdinalIgnoreCase) ||
							term.Key.EndsWith(normalized, StringComparison.OrdinalIgnoreCase)))
					.Take(2)
					.ToList();
				if (suffixMatches.Count == 1)
				{
					description = suffixMatches[0].Description;
					return description.Length > 0;
				}
			}

			return false;
		}

		public static string CleanMeaning(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return "";
			}

			string value = WebUtility.HtmlDecode(text)
				.Replace("\r", " ")
				.Replace("\n", " ")
				.Replace("\t", " ");
			value = Regex.Replace(value, @"(/\*|\*/|//)", " ");
			value = Regex.Replace(value, @"^[\s\*\-_=#:/\\|，。；;,.、]+", "");
			value = Regex.Replace(value, @"[\s\*\-_=#:/\\|，。；;,.、]+$", "");
			value = Regex.Replace(value, @"^(?:变量名|变量|名称|信号名|信号|点位|端口|引脚|说明|描述|含义|备注|功能|name|signal|desc|description)\s*[:：=,\-|]*\s*", "", RegexOptions.IgnoreCase);
			value = Regex.Replace(value, @"\s+", " ").Trim();
			if (value.Length > 60)
			{
				value = value.Substring(0, 60).Trim();
			}
			return value;
		}

		private static IEnumerable<string> BuildLookupKeys(string identifier)
		{
			string normalized = NormalizeDictionaryKey(identifier);
			if (normalized.Length > 0)
			{
				yield return normalized;
			}

			string baseName = GetIdentifierBase(identifier);
			string baseKey = NormalizeDictionaryKey(baseName);
			if (baseKey.Length > 0 && !baseKey.Equals(normalized, StringComparison.OrdinalIgnoreCase))
			{
				yield return baseKey;
			}

			string tailName = GetIdentifierTail(identifier);
			string tailKey = NormalizeDictionaryKey(tailName);
			if (tailKey.Length > 0 &&
				!tailKey.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
				!tailKey.Equals(baseKey, StringComparison.OrdinalIgnoreCase))
			{
				yield return tailKey;
			}

			foreach (Match match in Regex.Matches(identifier, @"(?:AI|AO|DI|DO)?_?Pin\d+", RegexOptions.IgnoreCase))
			{
				string pinKey = NormalizeDictionaryKey(match.Value);
				if (pinKey.Length > 0)
				{
					yield return pinKey;
				}
			}
		}

		private static string NormalizeDictionaryKey(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return "";
			}

			string value = Regex.Replace(text.Trim(), @"\[[^\]]+\]", "");
			value = Regex.Replace(value, @"^[^A-Za-z_]+|[^A-Za-z0-9_]+$", "");
			return GetIdentifierBase(value);
		}

		private sealed record BusinessTerm(string Key, string Description, string Source, int Score);

		private sealed class ProjectBusinessDictionaryBuilder
		{
			private const int MaxSourceFiles = 260;
			private const int MaxDocumentFiles = 80;
			private const long MaxDocumentBytes = 8L * 1024L * 1024L;

			private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
			{
				".git", ".svn", ".vs", "bin", "obj", "debug", "release", "listings", "objects", "rte",
				"__pycache__", "packages", ".idea"
			};

			private static readonly string[] DocumentExtensions =
			{
				".docx", ".xlsx", ".xlsm", ".xls", ".csv", ".txt", ".md", ".tsv"
			};

			private readonly string _workDirectory;
			private readonly Dictionary<string, BusinessTerm> _terms = new(StringComparer.OrdinalIgnoreCase);
			private readonly HashSet<string> _sourceFiles = new(StringComparer.OrdinalIgnoreCase);
			private DateTime _latestWriteUtc;

			public ProjectBusinessDictionaryBuilder(string workDirectory)
			{
				_workDirectory = workDirectory;
				Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			}

			public void ScanSourceComments()
			{
				foreach (string file in EnumerateSourceFiles(_workDirectory).Take(MaxSourceFiles))
				{
					_sourceFiles.Add(file);
					AddWriteTime(file);
					string text;
					try
					{
						text = ReadTextFile(file);
					}
					catch
					{
						continue;
					}

					ScanSourceCommentText(file, text);
				}
			}

			public void ScanDocuments()
			{
				foreach (string file in EnumerateDocumentFiles().Take(MaxDocumentFiles))
				{
					_sourceFiles.Add(file);
					AddWriteTime(file);
					foreach (string line in ReadDocumentLines(file).Take(4000))
					{
						AddTermsFromLine(line, "资料:" + Path.GetFileName(file), 70);
					}
				}
			}

			public ProjectBusinessDictionary Build()
			{
				string signatureSeed = _terms.Count.ToString(CultureInfo.InvariantCulture) +
					"|" + _sourceFiles.Count.ToString(CultureInfo.InvariantCulture) +
					"|" + _latestWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture);
				return new ProjectBusinessDictionary(
					new Dictionary<string, BusinessTerm>(_terms, StringComparer.OrdinalIgnoreCase),
					_sourceFiles.Count,
					signatureSeed.GetHashCode(StringComparison.OrdinalIgnoreCase).ToString("X8", CultureInfo.InvariantCulture));
			}

			private void ScanSourceCommentText(string file, string text)
			{
				string pendingComment = "";
				foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
				{
					string line = raw.Trim();
					if (line.Length == 0)
					{
						pendingComment = "";
						continue;
					}

					if (TryExtractLineComment(line, out string code, out string comment))
					{
						if (code.Trim().Length == 0)
						{
							pendingComment = LooksLikeCommentedOutCode(comment) ? "" : CleanMeaning(comment);
							continue;
						}

						AddTermsFromCodeAndComment(code, comment, "注释:" + Path.GetFileName(file), 95);
						pendingComment = "";
						continue;
					}

					string blockComment = ExtractSingleLineBlockComment(line, out string blockCode);
					if (blockComment.Length > 0)
					{
						if (blockCode.Trim().Length > 0)
						{
							AddTermsFromCodeAndComment(blockCode, blockComment, "注释:" + Path.GetFileName(file), 93);
						}
						else
						{
							pendingComment = LooksLikeCommentedOutCode(blockComment) ? "" : CleanMeaning(blockComment);
						}
						continue;
					}

					if (pendingComment.Length > 0)
					{
						AddTermsFromCodeAndComment(line, pendingComment, "注释:" + Path.GetFileName(file), 88);
						pendingComment = "";
					}
				}
			}

			private void AddTermsFromCodeAndComment(string code, string comment, string source, int score)
			{
				string meaning = CleanMeaning(comment);
				if (!IsUsefulMeaning(meaning))
				{
					return;
				}

				foreach (string identifier in ExtractIdentifiers(code).Take(8))
				{
					if (LooksLikeBusinessToken(identifier))
					{
						AddTerm(identifier, meaning, source, score);
					}
				}
			}

			private void AddTermsFromLine(string line, string source, int score)
			{
				string compact = CleanMeaning(line);
				if (compact.Length < 4)
				{
					return;
				}

				List<string> identifiers = ExtractIdentifiers(compact)
					.Where(LooksLikeBusinessToken)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.Take(10)
					.ToList();
				foreach (string identifier in identifiers)
				{
					string meaning = BuildMeaningFromLine(compact, identifier);
					if (IsUsefulMeaning(meaning))
					{
						AddTerm(identifier, meaning, source, score);
					}
				}
			}

			private void AddTerm(string identifier, string description, string source, int score)
			{
				string key = NormalizeDictionaryKey(identifier);
				description = CleanMeaning(description);
				if (key.Length == 0 || !IsUsefulMeaning(description))
				{
					return;
				}

				if (_terms.TryGetValue(key, out BusinessTerm? existing) &&
					existing.Score > score)
				{
					return;
				}

				_terms[key] = new BusinessTerm(key, description, source, score);

				foreach (string alias in BuildAliasKeys(key))
				{
					if (alias.Length == 0 || alias.Equals(key, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					if (!_terms.TryGetValue(alias, out existing) || existing.Score < score - 4)
					{
						_terms[alias] = new BusinessTerm(alias, description, source, score - 4);
					}
				}
			}

			private static IEnumerable<string> BuildAliasKeys(string key)
			{
				foreach (Match match in Regex.Matches(key, @"(?:AI|AO|DI|DO)?_?Pin\d+", RegexOptions.IgnoreCase))
				{
					yield return NormalizeDictionaryKey(match.Value);
					yield return NormalizeDictionaryKey(Regex.Replace(match.Value, @"^(?:AI|AO|DI|DO)_?", "", RegexOptions.IgnoreCase));
				}
			}

			private static string BuildMeaningFromLine(string line, string identifier)
			{
				string escaped = Regex.Escape(identifier);
				string meaning = Regex.Replace(line, @"\b" + escaped + @"\b", " ", RegexOptions.IgnoreCase);
				meaning = Regex.Replace(meaning, @"\b(?:u8|u16|u32|s8|s16|s32|uint8_t|uint16_t|uint32_t|int8_t|int16_t|int32_t|unsigned|signed|char|short|int|long|float|double|void|extern|static|volatile|const)\b", " ", RegexOptions.IgnoreCase);
				meaning = Regex.Replace(meaning, @"0x[0-9A-Fa-f]+|\b\d+\b", " ");
				meaning = Regex.Replace(meaning, @"[|,，;；:=\-_/\\]+", " ");
				return CleanMeaning(meaning);
			}

			private static bool TryExtractLineComment(string line, out string code, out string comment)
			{
				int index = line.IndexOf("//", StringComparison.Ordinal);
				if (index < 0)
				{
					code = "";
					comment = "";
					return false;
				}

				code = line.Substring(0, index);
				comment = line.Substring(index + 2);
				return true;
			}

			private static string ExtractSingleLineBlockComment(string line, out string code)
			{
				code = line;
				int start = line.IndexOf("/*", StringComparison.Ordinal);
				if (start < 0)
				{
					return "";
				}
				int end = line.IndexOf("*/", start + 2, StringComparison.Ordinal);
				if (end < 0)
				{
					return "";
				}

				code = line.Remove(start, end - start + 2);
				return line.Substring(start + 2, end - start - 2);
			}

			private static IEnumerable<string> ExtractIdentifiers(string text)
			{
				foreach (Match match in Regex.Matches(text, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
				{
					string value = match.Value;
					if (value.Length >= 2 && !IsCKeyword(value) && !IsMonitorInternalFunctionName(value))
					{
						yield return value;
					}
				}
			}

			private static bool LooksLikeBusinessToken(string token)
			{
				if (string.IsNullOrWhiteSpace(token) || token.Length < 3 || IsCKeyword(token) || IsMonitorInternalFunctionName(token))
				{
					return false;
				}

				string upper = token.ToUpperInvariant();
				return token.Contains('_', StringComparison.Ordinal) ||
					token.Any(char.IsDigit) ||
					upper.Contains("AI", StringComparison.Ordinal) ||
					upper.Contains("AO", StringComparison.Ordinal) ||
					upper.Contains("DI", StringComparison.Ordinal) ||
					upper.Contains("DO", StringComparison.Ordinal) ||
					upper.Contains("CAN", StringComparison.Ordinal) ||
					upper.Contains("LCD", StringComparison.Ordinal) ||
					upper.Contains("PWM", StringComparison.Ordinal) ||
					upper.Contains("MOTOR", StringComparison.Ordinal) ||
					upper.Contains("VALVE", StringComparison.Ordinal) ||
					upper.Contains("PRESS", StringComparison.Ordinal) ||
					upper.Contains("SENSOR", StringComparison.Ordinal);
			}

			private static bool LooksLikeCommentedOutCode(string comment)
			{
				string text = CleanMeaning(comment);
				if (text.Length == 0)
				{
					return false;
				}

				string codeLike = MaskCommentsAndLiteralsPreserveLength(text).Trim();
				if (Regex.IsMatch(codeLike, @"^\s*(?:if|else|for|while|switch|case|return|break|continue)\b", RegexOptions.IgnoreCase))
				{
					return true;
				}
				if (Regex.IsMatch(codeLike, @"^[A-Za-z_][A-Za-z0-9_]*\s*\([^;]*\)\s*;?$"))
				{
					return true;
				}
				if (Regex.IsMatch(codeLike, @"^[A-Za-z_][A-Za-z0-9_\.\[\]\-\>]*\s*(?:=|\+=|-=|\*=|/=|--|\+\+)", RegexOptions.IgnoreCase))
				{
					return true;
				}
				if (codeLike.Contains("{", StringComparison.Ordinal) || codeLike.Contains("}", StringComparison.Ordinal))
				{
					return true;
				}

				return false;
			}

			private static bool IsUsefulMeaning(string meaning)
			{
				if (string.IsNullOrWhiteSpace(meaning) || meaning.Length < 2)
				{
					return false;
				}
				if (Regex.IsMatch(meaning, @"^[A-Za-z0-9_\s|,，;；:=\-_/\\]+$"))
				{
					return false;
				}
				if (Regex.IsMatch(meaning, @"^(?:0|1|true|false|null)$", RegexOptions.IgnoreCase))
				{
					return false;
				}
				return true;
			}

			private IEnumerable<string> EnumerateSourceFiles(string root)
			{
				return EnumerateFiles(root, maxDepth: 8)
					.Where(file =>
					{
						string extension = Path.GetExtension(file);
						return extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
							extension.Equals(".h", StringComparison.OrdinalIgnoreCase);
					});
			}

			private IEnumerable<string> EnumerateDocumentFiles()
			{
				var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach ((string Root, int Depth) root in BuildDocumentRoots())
				{
					foreach (string file in EnumerateFiles(root.Root, root.Depth))
					{
						if (!seen.Add(file))
						{
							continue;
						}
						string name = Path.GetFileName(file);
						if (name.StartsWith("~$", StringComparison.Ordinal) || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						string extension = Path.GetExtension(file);
						if (!DocumentExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
						{
							continue;
						}
						try
						{
							FileInfo info = new FileInfo(file);
							if (info.Length <= 0 || info.Length > MaxDocumentBytes)
							{
								continue;
							}
						}
						catch
						{
							continue;
						}
						yield return file;
					}
				}
			}

			private IEnumerable<(string Root, int Depth)> BuildDocumentRoots()
			{
				yield return (_workDirectory, 6);

				string? parent = Directory.GetParent(_workDirectory)?.FullName;
				if (parent == null)
				{
					yield break;
				}

				yield return (parent, 2);
				IEnumerable<string> siblings;
				try
				{
					siblings = Directory.EnumerateDirectories(parent);
				}
				catch
				{
					yield break;
				}

				foreach (string sibling in siblings)
				{
					string name = Path.GetFileName(sibling);
					if (IsLikelyDocumentDirectoryName(name))
					{
						yield return (sibling, 5);
					}
				}
			}

			private static bool IsLikelyDocumentDirectoryName(string name)
			{
				string upper = name.ToUpperInvariant();
				string[] tokens =
				{
					"DOC", "DATA", "SPEC", "IO", "CAN", "LCD", "资料", "文档", "设计", "协议", "说明", "硬件", "点表", "针脚", "引脚"
				};
				return tokens.Any(token => upper.Contains(token.ToUpperInvariant(), StringComparison.Ordinal));
			}

			private IEnumerable<string> EnumerateFiles(string root, int maxDepth)
			{
				if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
				{
					yield break;
				}

				var pending = new Stack<(string Directory, int Depth)>();
				pending.Push((root, 0));
				while (pending.Count > 0)
				{
					(string directory, int depth) = pending.Pop();
					IEnumerable<string> files;
					try
					{
						files = Directory.EnumerateFiles(directory);
					}
					catch
					{
						continue;
					}

					foreach (string file in files)
					{
						yield return file;
					}

					if (depth >= maxDepth)
					{
						continue;
					}

					IEnumerable<string> children;
					try
					{
						children = Directory.EnumerateDirectories(directory);
					}
					catch
					{
						continue;
					}

					foreach (string child in children)
					{
						if (!IgnoredDirectories.Contains(Path.GetFileName(child)))
						{
							pending.Push((child, depth + 1));
						}
					}
				}
			}

			private IEnumerable<string> ReadDocumentLines(string file)
			{
				string extension = Path.GetExtension(file);
				try
				{
					if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
					{
						return ReadDocxLines(file);
					}
					if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
						extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
					{
						return ReadXlsxLines(file);
					}
					if (extension.Equals(".xls", StringComparison.OrdinalIgnoreCase))
					{
						return ReadLegacyXlsLines(file);
					}
					return ReadTextFile(file)
						.Replace("\r\n", "\n")
						.Replace('\r', '\n')
						.Split('\n')
						.Select(CleanMeaning)
						.Where(line => line.Length > 0)
						.ToList();
				}
				catch
				{
					return Array.Empty<string>();
				}
			}

			private static IReadOnlyList<string> ReadDocxLines(string file)
			{
				using ZipArchive archive = ZipFile.OpenRead(file);
				ZipArchiveEntry? entry = archive.GetEntry("word/document.xml");
				if (entry == null)
				{
					return Array.Empty<string>();
				}

				using Stream stream = entry.Open();
				XDocument doc = XDocument.Load(stream);
				XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
				return doc.Descendants(w + "p")
					.Select(paragraph => CleanMeaning(string.Concat(paragraph.Descendants(w + "t").Select(t => t.Value))))
					.Where(line => line.Length > 0)
					.ToList();
			}

			private static IReadOnlyList<string> ReadXlsxLines(string file)
			{
				using ZipArchive archive = ZipFile.OpenRead(file);
				List<string> sharedStrings = ReadSharedStrings(archive);
				var rows = new List<string>();
				foreach (ZipArchiveEntry sheet in archive.Entries
					.Where(entry => entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) &&
						entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
					.Take(12))
				{
					using Stream stream = sheet.Open();
					XDocument doc = XDocument.Load(stream);
					XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
					foreach (XElement row in doc.Descendants(ns + "row"))
					{
						List<string> cells = row.Elements(ns + "c")
							.Select(cell => ReadCellText(cell, ns, sharedStrings))
							.Select(CleanMeaning)
							.Where(value => value.Length > 0)
							.Take(12)
							.ToList();
						if (cells.Count > 0)
						{
							rows.Add(string.Join(" | ", cells));
						}
					}
				}
				return rows;
			}

			private static IReadOnlyList<string> ReadLegacyXlsLines(string file)
			{
				byte[] data = File.ReadAllBytes(file);
				var rows = new List<string>();
				var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (string text in ExtractGbkStrings(data)
					.Concat(ExtractUtf16LeStrings(data))
					.Select(CleanMeaning)
					.Where(line => line.Length >= 3 && line.Length <= 160))
				{
					if (seen.Add(text))
					{
						rows.Add(text);
					}
					if (rows.Count >= 4000)
					{
						break;
					}
				}
				return rows;
			}

			private static IEnumerable<string> ExtractGbkStrings(byte[] data)
			{
				var buffer = new List<byte>(128);
				Encoding gbk = Encoding.GetEncoding(936);
				foreach (byte value in data)
				{
					if ((value >= 0x20 && value <= 0x7E) || value >= 0xA1)
					{
						buffer.Add(value);
						continue;
					}

					if (buffer.Count >= 4)
					{
						yield return gbk.GetString(buffer.ToArray());
					}
					buffer.Clear();
				}

				if (buffer.Count >= 4)
				{
					yield return gbk.GetString(buffer.ToArray());
				}
			}

			private static IEnumerable<string> ExtractUtf16LeStrings(byte[] data)
			{
				var builder = new StringBuilder();
				for (int i = 0; i + 1 < data.Length; i += 2)
				{
					char value = (char)(data[i] | (data[i + 1] << 8));
					if (!char.IsControl(value) && value != '\uffff')
					{
						builder.Append(value);
						continue;
					}

					if (builder.Length >= 4)
					{
						yield return builder.ToString();
					}
					builder.Clear();
				}

				if (builder.Length >= 4)
				{
					yield return builder.ToString();
				}
			}

			private static List<string> ReadSharedStrings(ZipArchive archive)
			{
				ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
				if (entry == null)
				{
					return new List<string>();
				}

				using Stream stream = entry.Open();
				XDocument doc = XDocument.Load(stream);
				XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
				return doc.Descendants(ns + "si")
					.Select(si => CleanMeaning(string.Concat(si.Descendants(ns + "t").Select(t => t.Value))))
					.ToList();
			}

			private static string ReadCellText(XElement cell, XNamespace ns, IReadOnlyList<string> sharedStrings)
			{
				string type = cell.Attribute("t")?.Value ?? "";
				if (type.Equals("s", StringComparison.OrdinalIgnoreCase))
				{
					string value = cell.Element(ns + "v")?.Value ?? "";
					return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) &&
						index >= 0 &&
						index < sharedStrings.Count
							? sharedStrings[index]
							: "";
				}

				if (type.Equals("inlineStr", StringComparison.OrdinalIgnoreCase))
				{
					return string.Concat(cell.Descendants(ns + "t").Select(t => t.Value));
				}

				return cell.Element(ns + "v")?.Value ?? "";
			}

			private static string ReadTextFile(string file)
			{
				byte[] bytes = File.ReadAllBytes(file);
				if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
				{
					return Encoding.UTF8.GetString(bytes);
				}
				if (bytes.Length >= 2)
				{
					if (bytes[0] == 0xFF && bytes[1] == 0xFE)
					{
						return Encoding.Unicode.GetString(bytes);
					}
					if (bytes[0] == 0xFE && bytes[1] == 0xFF)
					{
						return Encoding.BigEndianUnicode.GetString(bytes);
					}
				}

				try
				{
					return new UTF8Encoding(false, true).GetString(bytes);
				}
				catch (DecoderFallbackException)
				{
					return Encoding.GetEncoding("GB18030").GetString(bytes);
				}
			}

			private void AddWriteTime(string file)
			{
				try
				{
					DateTime write = File.GetLastWriteTimeUtc(file);
					if (write > _latestWriteUtc)
					{
						_latestWriteUtc = write;
					}
				}
				catch
				{
				}
			}
		}
	}
}
