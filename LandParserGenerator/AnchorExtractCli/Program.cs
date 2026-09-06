using System.Globalization;
using System.Text;
using MarkupServer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// =====================================================================================
// AnchorExtractCli — извлечение якорей GraphQL штатным путём MarkupServer
// (грамматика graphql.land, тот же код GetTreeNodeFromGqlFieldNode), без плагина.
//
//   AnchorExtractCli --gql <graphql.land> --ts <ts_resolvers.land> --jobs <jobs.json>
//   AnchorExtractCli --gql ... --ts ... --folder <dir> --out <anchors.json> [--rel-root <dir>]
//
// jobs.json: [{"folder": "...", "out": "...", "relRoot": "..."}, ...]
// Для каждой папки: ListAnchors(forceRescan) → читаем .land/anchors.json → пишем список
// полей (AnchorKind == gqlField) в формате экспериментов (как 01_parse_graphql.py):
//   Id = "<file>::<ParentRaw>.<Name>#<k>", File, Kind, MethodNameNorm, ParentNameNorm,
//   ParentNameRaw, NameRaw, ReturnTypeNorm, Args[{TypeNorm,NameNorm}], OrdinalInParent, StartOffset, EndOffset
// Сервер сканирует только *.graphql — вызывающая сторона переименовывает .gql/.graphqls.
// =====================================================================================

static class Program
{
	static async Task<int> Main(string[] argv)
	{
		CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
		var a = new Dictionary<string, string>(StringComparer.Ordinal);
		string key = null;
		foreach (var s in argv)
		{
			if (s.StartsWith("--")) { key = s.Substring(2); a[key] = "true"; }
			else if (key != null) { a[key] = s; key = null; }
		}
		if (!a.ContainsKey("gql") || !a.ContainsKey("ts") || !(a.ContainsKey("jobs") || a.ContainsKey("folder")))
		{
			Console.Error.WriteLine("usage: AnchorExtractCli --gql graphql.land --ts ts_resolvers.land (--jobs jobs.json | --folder dir --out anchors.json [--rel-root dir])");
			return 2;
		}

		Environment.SetEnvironmentVariable("LAND_STITCH_DISABLE", "1");

		var jobs = new List<(string folder, string outPath, string relRoot)>();
		if (a.TryGetValue("jobs", out var jobsPath))
		{
			var arr = JArray.Parse(File.ReadAllText(jobsPath));
			foreach (var j in arr)
				jobs.Add((j["folder"]!.ToString(), j["out"]!.ToString(), j["relRoot"]?.ToString()));
		}
		else
		{
			jobs.Add((a["folder"], a["out"], a.TryGetValue("rel-root", out var rr) ? rr : null));
		}

		var svc = new LandService();
		await svc.InitializeAsync(new InitializeParams
		{
			protocolVersion = 2,
			offsetEncoding = "utf-16",
			graphqlParserPath = Path.GetFullPath(a["gql"]),
			typescriptParserPath = Path.GetFullPath(a["ts"]),
		});

		int done = 0, failed = 0;
		foreach (var (folder, outPath, relRoot) in jobs)
		{
			try
			{
				var full = Path.GetFullPath(folder);
				var storePath = Path.Combine(full, ".land", "anchors.json");
				if (File.Exists(storePath)) File.Delete(storePath);

				await svc.ListAnchorsAsync(new ListTreeParams { folderPath = full, preferCache = false, forceRescan = true });

				if (!File.Exists(storePath))
					throw new InvalidOperationException("server did not write " + storePath);

				var jo = JObject.Parse(File.ReadAllText(storePath, Encoding.UTF8));
				var anchors = (JArray)(jo["Anchors"] ?? new JArray());
				var root = Path.GetFullPath(relRoot ?? full);

				var counters = new Dictionary<string, int>(StringComparer.Ordinal);
				var outList = new List<JObject>();
				foreach (var n in anchors.OfType<JObject>())
				{
					if (!string.Equals(n["NodeType"]?.ToString(), "anchor", StringComparison.OrdinalIgnoreCase)) continue;
					if (!string.Equals(n["AnchorKind"]?.ToString(), "gqlField", StringComparison.OrdinalIgnoreCase)) continue;

					var file = Path.GetRelativePath(root, n["Filepath"]!.ToString()).Replace('\\', '/');
					var parentRaw = n["ParentNameRaw"]?.ToString() ?? "";
					var nameRaw = n["Name"]?.ToString() ?? "";
					var ck = file + "" + parentRaw;
					counters[ck] = counters.TryGetValue(ck, out var c) ? c + 1 : 1;
					var args = new JArray();
					foreach (var ar in (n["Args"] as JArray ?? new JArray()).OfType<JObject>())
						args.Add(new JObject { ["TypeNorm"] = ar["TypeNorm"]?.ToString() ?? "", ["NameNorm"] = ar["NameNorm"]?.ToString() ?? "" });

					outList.Add(new JObject
					{
						["Id"] = $"{file}::{parentRaw}.{nameRaw}#{counters[ck]}",
						["File"] = file,
						["Kind"] = n["GqlTypeKind"]?.ToString() ?? "",
						["NameRaw"] = nameRaw,
						["ParentNameRaw"] = parentRaw,
						["MethodNameNorm"] = n["MethodNameNorm"]?.ToString() ?? "",
						["ParentNameNorm"] = n["ParentNameNorm"]?.ToString() ?? "",
						["ReturnTypeNorm"] = n["ReturnTypeNorm"]?.ToString() ?? "",
						["Args"] = args,
						["OrdinalInParent"] = n["OrdinalInParent"]?.Type == JTokenType.Integer ? (int)n["OrdinalInParent"] : 0,
						["StartOffset"] = n["StartOffset"]?.Type == JTokenType.Integer ? (int)n["StartOffset"] : 0,
						["EndOffset"] = n["EndOffset"]?.Type == JTokenType.Integer ? (int)n["EndOffset"] : 0,
					});
				}

				// канонический порядок вывода: файл, смещение
				outList = outList.OrderBy(x => x["File"]!.ToString(), StringComparer.Ordinal).ThenBy(x => (int)x["StartOffset"]!).ToList();

				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
				File.WriteAllText(outPath, JsonConvert.SerializeObject(outList, Formatting.None), new UTF8Encoding(false));
				done++;
				Console.Error.WriteLine($"[extract] {folder} -> {outPath}: {outList.Count} fields");
			}
			catch (Exception ex)
			{
				failed++;
				Console.Error.WriteLine($"[extract] FAILED {folder}: {ex.Message}");
			}
		}
		Console.Error.WriteLine($"[extract] done={done} failed={failed}");
		return failed == 0 ? 0 : 1;
	}
}
