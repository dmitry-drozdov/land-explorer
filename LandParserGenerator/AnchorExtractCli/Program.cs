using System.Diagnostics;
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
//
// --go golang_markup.land --lang go: функции и методы Go (AnchorKind == goFunc, LandService.Go.cs; e20_go_real_history):
//   Id = "<file>::<Receiver>.<Name>#<k>" у метода, "<file>::<Name>#<k>" у функции, k — номер вхождения (файл, получатель, имя);
//   Kind = method | func, ReceiverRaw — базовый тип получателя ("" у функции), ParentNameRaw — получатель или имя пакета.
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
		if (a.ContainsKey("stitch"))
			return await Stitch(a);
		if (!a.ContainsKey("gql") || !a.ContainsKey("ts") || !(a.ContainsKey("jobs") || a.ContainsKey("folder")))
		{
			Console.Error.WriteLine("usage: AnchorExtractCli --gql graphql.land --ts ts_resolvers.land (--jobs jobs.json | --folder dir --out anchors.json [--rel-root dir])");
			return 2;
		}

		Environment.SetEnvironmentVariable("LAND_STITCH_DISABLE", "1");

		// --timing out.csv [--repeats R]: время сканирования по заданиям (разбор LanD / извлечение якорей / мешки), медианы по R повторам;
		// извлечение = всё сканирование минус разбор и мешки (LandService.ScanTimings). Для замеров полного цикла (e11/e12 extract_cost).
		var timingPath = a.TryGetValue("timing", out var tp) ? tp : null;
		int repeats = a.TryGetValue("repeats", out var rp) ? Math.Max(1, int.Parse(rp, CultureInfo.InvariantCulture)) : 1;
		var timingRows = new List<string>();

		var jobs = new List<(string folder, string outPath, string relRoot, string label)>();
		if (a.TryGetValue("jobs", out var jobsPath))
		{
			var arr = JArray.Parse(File.ReadAllText(jobsPath));
			foreach (var j in arr)
				jobs.Add((j["folder"]!.ToString(), j["out"]!.ToString(), j["relRoot"]?.ToString(), j["label"]?.ToString()));
		}
		else
		{
			jobs.Add((a["folder"], a["out"], a.TryGetValue("rel-root", out var rr) ? rr : null, null));
		}

		var svc = new LandService();
		await svc.InitializeAsync(new InitializeParams
		{
			protocolVersion = 2,
			offsetEncoding = "utf-16",
			graphqlParserPath = Path.GetFullPath(a["gql"]),
			typescriptParserPath = Path.GetFullPath(a["ts"]),
			goParserPath = a.TryGetValue("go", out var goGrammar) ? Path.GetFullPath(goGrammar) : null,
		});
		var langGo = a.TryGetValue("lang", out var lang) && string.Equals(lang, "go", StringComparison.OrdinalIgnoreCase);
		var anchorKind = langGo ? "goFunc" : "gqlField";

		int done = 0, failed = 0;
		foreach (var (folder, outPath, relRoot, label) in jobs)
		{
			try
			{
				var full = Path.GetFullPath(folder);
				var storePath = Path.Combine(full, ".land", "anchors.json");
				var parseMs = new List<double>(); var extractMs = new List<double>(); var bagsMs = new List<double>(); var scanMs = new List<double>(); var listMs = new List<double>();
				for (int rep = 0; rep < repeats; rep++)
				{
					if (File.Exists(storePath)) File.Delete(storePath);
					var swList = Stopwatch.StartNew();
					await svc.ListAnchorsAsync(new ListTreeParams { folderPath = full, preferCache = false, forceRescan = true });
					swList.Stop();
					listMs.Add(swList.Elapsed.TotalMilliseconds);
					var t = svc.LastScanTimings;
					if (t != null) { parseMs.Add(t.ParseMs); extractMs.Add(t.ExtractMs); bagsMs.Add(t.BagsMs); scanMs.Add(t.TotalMs); }
				}

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
					if (!string.Equals(n["AnchorKind"]?.ToString(), anchorKind, StringComparison.OrdinalIgnoreCase)) continue;

					var file = Path.GetRelativePath(root, n["Filepath"]!.ToString()).Replace('\\', '/');
					if (langGo)
					{
						outList.Add(GoRecord(n, file, counters));
						continue;
					}
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
				if (timingPath != null && parseMs.Count > 0)
					timingRows.Add(string.Join(",", Csv(label ?? folder), Csv(folder), outList.Count.ToString(CultureInfo.InvariantCulture), repeats.ToString(CultureInfo.InvariantCulture),
						Median(parseMs), Median(extractMs), Median(bagsMs), Median(scanMs), Median(listMs)));
				done++;
				Console.Error.WriteLine($"[extract] {folder} -> {outPath}: {outList.Count} fields");
			}
			catch (Exception ex)
			{
				failed++;
				Console.Error.WriteLine($"[extract] FAILED {folder}: {ex.Message}");
			}
		}
		if (timingPath != null)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(timingPath)) ?? ".");
			File.WriteAllLines(timingPath, new[] { "label,folder,n_fields,repeats,ms_parse,ms_extract,ms_bags,ms_scan_total,ms_list_anchors" }.Concat(timingRows), new UTF8Encoding(false));
		}
		Console.Error.WriteLine($"[extract] done={done} failed={failed}");
		return failed == 0 ? 0 : 1;
	}

	/// --stitch jobs.json --out decisions.csv [--work DIR] (+ --gql --ts [--go] как обычно) — решения настоящей перепривязки
	/// сервера на паре версий (e21_server_stitch_mode, сверка стенда с сервером). Для пары: в рабочую папку кладётся старая
	/// версия, ListAnchors строит разметку; файлы заменяются новой версией, ListAnchors вызывается снова — сервер сшивает Id
	/// (StitchAutoAnchorIdsFromPrevious: слой 1 и слой 2). Старая точка перепривязана к той новой, которой достался её Id.
	/// jobs.json: [{"label", "old_dir", "new_dir"}]; выход: label,kind,old_file,old_offset,new_file,new_offset (пусто — не перепривязана).
	static async Task<int> Stitch(Dictionary<string, string> a)
	{
		Environment.SetEnvironmentVariable("LAND_STITCH_DISABLE", null);
		var svc = new LandService();
		await svc.InitializeAsync(new InitializeParams
		{
			protocolVersion = 2,
			offsetEncoding = "utf-16",
			graphqlParserPath = Path.GetFullPath(a["gql"]),
			typescriptParserPath = Path.GetFullPath(a["ts"]),
			goParserPath = a.TryGetValue("go", out var goGrammar) ? Path.GetFullPath(goGrammar) : null,
		});
		var work = Path.GetFullPath(a.TryGetValue("work", out var wd) ? wd : Path.Combine(Path.GetTempPath(), "e21_stitch"));
		var rows = new List<string> { "label,kind,old_file,old_offset,new_file,new_offset" };
		int done = 0, failed = 0;
		foreach (var j in JArray.Parse(File.ReadAllText(a["stitch"])).OfType<JObject>())
		{
			var label = j["label"]!.ToString();
			try
			{
				if (Directory.Exists(work)) Directory.Delete(work, true);
				Directory.CreateDirectory(work);
				CopyTree(j["old_dir"]!.ToString(), work);
				await svc.ListAnchorsAsync(new ListTreeParams { folderPath = work, preferCache = false, forceRescan = true });
				var before = ReadAnchors(work);
				foreach (var d in Directory.GetDirectories(work))
					if (!string.Equals(Path.GetFileName(d), ".land", StringComparison.OrdinalIgnoreCase)) Directory.Delete(d, true);
				foreach (var f in Directory.GetFiles(work)) File.Delete(f);
				CopyTree(j["new_dir"]!.ToString(), work);
				await svc.ListAnchorsAsync(new ListTreeParams { folderPath = work, preferCache = false, forceRescan = true });
				var after = ReadAnchors(work);
				foreach (var (id, o) in before)
				{
					after.TryGetValue(id, out var n);
					rows.Add(string.Join(",", Csv(label), Csv(o.kind), Csv(o.file), o.offset.ToString(CultureInfo.InvariantCulture),
						Csv(n.file ?? ""), n.file == null ? "" : n.offset.ToString(CultureInfo.InvariantCulture)));
				}
				done++;
				Console.Error.WriteLine($"[stitch] {label}: before {before.Count}, kept ids {before.Keys.Count(after.ContainsKey)}");
			}
			catch (Exception ex)
			{
				failed++;
				Console.Error.WriteLine($"[stitch] FAILED {label}: {ex.GetType().Name}: {ex.Message}");
			}
		}
		File.WriteAllLines(a["out"], rows, new UTF8Encoding(false));
		Console.Error.WriteLine($"[stitch] done={done} failed={failed}");
		return failed == 0 ? 0 : 1;

		Dictionary<string, (string kind, string file, int offset)> ReadAnchors(string root)
		{
			var jo = JObject.Parse(File.ReadAllText(Path.Combine(root, ".land", "anchors.json"), Encoding.UTF8));
			var res = new Dictionary<string, (string kind, string file, int offset)>(StringComparer.Ordinal);
			foreach (var n in ((JArray)(jo["Anchors"] ?? new JArray())).OfType<JObject>())
			{
				if (!string.Equals(n["NodeType"]?.ToString(), "anchor", StringComparison.OrdinalIgnoreCase)) continue;
				var kind = n["AnchorKind"]?.ToString() ?? "";
				if (kind != "gqlField" && kind != "goFunc") continue;
				var file = Path.GetRelativePath(root, n["Filepath"]!.ToString()).Replace('\\', '/');
				res[n["Id"]!.ToString()] = (kind, file, n["StartOffset"]?.Type == JTokenType.Integer ? (int)n["StartOffset"] : -1);
			}
			return res;
		}
	}

	static void CopyTree(string src, string dst)
	{
		src = Path.GetFullPath(src);
		foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
		{
			var rel = Path.GetRelativePath(src, f);
			if (rel.StartsWith(".land", StringComparison.OrdinalIgnoreCase)) continue;
			var t = Path.Combine(dst, rel);
			Directory.CreateDirectory(Path.GetDirectoryName(t)!);
			File.Copy(f, t, true);
		}
	}

	/// Запись точки Go в формате экспериментов; Name сервера — «Получатель.Имя» у метода и «Имя» у функции.
	static JObject GoRecord(JObject n, string file, Dictionary<string, int> counters)
	{
		var display = n["Name"]?.ToString() ?? "";
		var dot = display.LastIndexOf('.');
		var receiver = dot >= 0 ? display.Substring(0, dot) : "";
		var nameRaw = dot >= 0 ? display.Substring(dot + 1) : display;
		var ck = file + "" + receiver + "" + nameRaw;
		counters[ck] = counters.TryGetValue(ck, out var c) ? c + 1 : 1;
		var args = new JArray();
		foreach (var ar in (n["Args"] as JArray ?? new JArray()).OfType<JObject>())
			args.Add(new JObject { ["TypeNorm"] = ar["TypeNorm"]?.ToString() ?? "", ["NameNorm"] = ar["NameNorm"]?.ToString() ?? "" });
		var rec = new JObject
		{
			["Id"] = receiver.Length > 0 ? $"{file}::{receiver}.{nameRaw}#{counters[ck]}" : $"{file}::{nameRaw}#{counters[ck]}",
			["File"] = file,
			["Kind"] = receiver.Length > 0 ? "method" : "func",
			["NameRaw"] = nameRaw,
			["ReceiverRaw"] = receiver,
			["ParentNameRaw"] = n["ParentNameRaw"]?.ToString() ?? "",
			["MethodNameNorm"] = n["MethodNameNorm"]?.ToString() ?? "",
			["ParentNameNorm"] = n["ParentNameNorm"]?.ToString() ?? "",
			["ReturnTypeNorm"] = n["ReturnTypeNorm"]?.ToString() ?? "",
			["Args"] = args,
			["OrdinalInParent"] = n["OrdinalInParent"]?.Type == JTokenType.Integer ? (int)n["OrdinalInParent"] : 0,
			["StartOffset"] = n["StartOffset"]?.Type == JTokenType.Integer ? (int)n["StartOffset"] : 0,
			["EndOffset"] = n["EndOffset"]?.Type == JTokenType.Integer ? (int)n["EndOffset"] : 0,
		};
		// отпечаток тела (e24) — только если сервер его записал; прежние выгрузки не меняются
		if (n["BodySig"] is JValue sig && sig.Type == JTokenType.String)
			rec["BodySig"] = sig.ToString();
		return rec;
	}

	static string Median(List<double> xs)
	{
		var s = xs.OrderBy(x => x).ToList();
		double m = s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
		return m.ToString("R", CultureInfo.InvariantCulture);
	}

	static string Csv(string s)
	{
		s ??= "";
		return s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
	}
}
