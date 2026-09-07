// =====================================================================================
// CfBench — стенд для ОРИГИНАЛЬНОГО алгоритма перепривязки Головешкина (Land.Markup.Binding.ContextFinder)
// на парах версий GraphQL-схем корпуса реальной истории (markup/experiments/e03_real_history_corpus).
//
// Для каждой пары версий (job):
//   1. разбираем все *.graphql старой и новой версии парсером LanD (Builder.BuildParser по graphql.land);
//   2. MarkupManager.AddLand(старый файл) — точки привязки для всех land-сущностей старой версии
//      (как команда «добавить всё» в панели разметки); оставляем точки-поля (type_line, func_line);
//   3. как в MarkupManager.Remap(searchArea, allowAutoDecisions: true, SearchType.Global) панели:
//      ContextFinder.FindPoints(points, newFiles, Local) → для точек без авторешения FindPoints(..., Global);
//   4. пишем по точкам: старый Id, Id лучшего кандидата (по файлу и смещению узла в новой версии),
//      похожести первого и второго кандидата, флаг IsAuto; по парам — время каждого этапа.
// Соответствие Id — через anchors/*.json корпуса: (файл, StartOffset узла) → Id.
//
// usage: CfBench --grammar graphql.land --jobs jobs.json --out pairs.csv --per-query pq.csv [--single-core | --core N] [--no-heuristic] [--only label]
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Land.Core;
using Land.Core.Parsing;
using Land.Core.Parsing.Tree;
using Land.Core.Specification;
using Land.Markup;
using Land.Markup.Binding;
using Land.Markup.CoreExtension;
using Land.Control;   // Cache объявлен в этом пространстве имён внутри Land.Markup.dll
using Newtonsoft.Json.Linq;

namespace CfBench
{
	static class Program
	{
		static readonly HashSet<string> FieldTypes = new HashSet<string> { "type_line", "func_line" };
		static bool NoHeuristic;

		static int Main(string[] argv)
		{
			CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
			CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
			var a = ParseArgs(argv);
			if (!a.ContainsKey("grammar") || !a.ContainsKey("jobs") || !a.ContainsKey("out"))
			{
				Console.Error.WriteLine("usage: CfBench --grammar graphql.land --jobs jobs.json --out pairs.csv [--per-query pq.csv] [--single-core | --core N] [--no-heuristic] [--only label]");
				return 2;
			}
			// --single-core: одно ядро (CPU 0); --core N: одно ядро с номером N (для параллельных однопоточных прогонов
			// разных пар на разных ядрах — каждый процесс получает своё физическое ядро)
			if (a.ContainsKey("core"))
				Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)(1L << int.Parse(a["core"]));
			else if (a.ContainsKey("single-core"))
				Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)1;
			NoHeuristic = a.ContainsKey("no-heuristic");

			var messages = new List<Message>();
			var parser = Builder.BuildParser(GrammarType.LR, File.ReadAllText(a["grammar"]), messages);
			if (messages.Any(m => m.Type == MessageType.Error))
			{
				Console.Error.WriteLine("grammar error: " + messages.First(m => m.Type == MessageType.Error).Text);
				return 1;
			}

			// как в панели (Control/Parsing/ParserLoader.cs): после разбора дерево размечается опциями %markup (land, headercore, ...)
			parser.SetVisitor(g => new MarkupOptionsProcessingVisitor(g));

			var jobs = JArray.Parse(File.ReadAllText(a["jobs"]));
			var only = a.ContainsKey("only") ? a["only"] : null;
			var pairRows = new List<string>();
			var pqRows = new List<string>();
			int done = 0, failed = 0;
			var swAll = Stopwatch.StartNew();
			foreach (var job in jobs.OfType<JObject>())
			{
				var label = job["label"].ToString();
				if (only != null && label != only) continue;
				try
				{
					RunJob(parser, job, pairRows, pqRows);
					done++;
				}
				catch (Exception ex)
				{
					failed++;
					var inner = ex is AggregateException ag ? ag.Flatten().InnerExceptions.Cast<Exception>().ToList() : new List<Exception> { ex };
					Console.Error.WriteLine($"[cfbench] FAILED {label}: " + string.Join(" | ", inner.Select(e => e.GetType().Name + ": " + e.Message)));
					if (Environment.GetEnvironmentVariable("CFBENCH_DEBUG") == "1")
						foreach (var e in inner) Console.Error.WriteLine(e.StackTrace);
					pairRows.Add(Csv(label, "", "", "", "", "", "", "", "", "", "", "", "", "error: " + ex.GetType().Name));
				}
				if ((done + failed) % 20 == 0)
					Console.Error.WriteLine($"[cfbench] {done + failed}/{jobs.Count} jobs, {swAll.Elapsed.TotalSeconds:F0}s");
			}
			WriteCsv(a["out"], "label,n_old_files,n_new_files,n_points,n_points_with_id,n_auto_local,n_auto_total,ms_parse_old,ms_parse_new,ms_addland,ms_local,ms_global,ms_total,note", pairRows);
			if (a.ContainsKey("per-query"))
				WriteCsv(a["per-query"], "label,old_id,point_type,search,top1_id,top1_file,top1_offset,sim1,sim2,is_auto,n_candidates", pqRows);
			Console.Error.WriteLine($"[cfbench] done={done} failed={failed} in {swAll.Elapsed.TotalSeconds:F0}s");
			return failed == 0 ? 0 : 1;
		}

		static void RunJob(BaseParser parser, JObject job, List<string> pairRows, List<string> pqRows)
		{
			var label = job["label"].ToString();
			var oldDir = Path.GetFullPath(job["old_dir"].ToString());
			var newDir = Path.GetFullPath(job["new_dir"].ToString());
			var oldIds = LoadAnchorIds(job["old_anchors"].ToString());
			var newIds = LoadAnchorIds(job["new_anchors"].ToString());

			var sw = Stopwatch.StartNew();
			var oldFiles = ParseDir(parser, oldDir);
			double msParseOld = sw.Elapsed.TotalMilliseconds;
			sw.Restart();
			var newFiles = ParseDir(parser, newDir);
			double msParseNew = sw.Elapsed.TotalMilliseconds;

			// имена файлов — относительные пути, одинаковые для старой и новой версии (файл «изменился на месте»,
			// как в панели); GetParsed отдаёт старую версию во время создания точек и новую — во время перепривязки
			var current = oldFiles.ToDictionary(f => f.Name, f => f);
			Func<string, ParsedFile> getParsed = name => current.TryGetValue(name, out var pf) ? pf : null;
			// --no-heuristic: без эвристики точного совпадения (PreHeuristic = null) — поиск для всех точек,
			// сопоставимо с нашим стендом knn-batch, где каждый запрос ищется в дереве
			var mm = new MarkupManager(getParsed, NoHeuristic ? null : new ContextsEqualityHeuristic());

			sw.Restart();
			foreach (var f in oldFiles)
				mm.AddLand(f);
			double msAddLand = sw.Elapsed.TotalMilliseconds;

			var allPoints = mm.GetConcernPoints();
			if (Environment.GetEnvironmentVariable("CFBENCH_DEBUG") == "1")
			{
				Console.Error.WriteLine("  all points: " + allPoints.Count + " types: " + string.Join(",", allPoints.GroupBy(p => p.Context.Type).Select(g => g.Key + ":" + g.Count())));
				var hist = new Dictionary<string, int>();
				void Walk(Node n, int depth) { if (n == null || depth > 3) return; var k = depth + ":" + n.Type; hist[k] = hist.TryGetValue(k, out var c) ? c + 1 : 1; foreach (var ch in n.Children ?? new List<Node>()) Walk(ch, depth + 1); }
				foreach (var f in oldFiles) Walk(f.Root, 0);
				Console.Error.WriteLine("  tree: " + string.Join(", ", hist.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)));
			}
			var points = allPoints.Where(p => FieldTypes.Contains(p.Context.Type)).ToList();
			var pointId = new Dictionary<ConcernPoint, string>();
			foreach (var p in points)
			{
				if (oldIds.TryGetValue((p.Context.FileName, p.Context.StartOffset), out var id))
					pointId[p] = id;
			}

			current = newFiles.ToDictionary(f => f.Name, f => f);
			var cache = new Dictionary<ParsedFile, Cache>();
			var cf = mm.ContextFinder;

			// локальный поиск (тот же файл), затем глобальный для точек без авторешения — как MarkupManager.Remap(..., Global)
			sw.Restart();
			var local = cf.FindPoints(points, newFiles, ContextFinder.SearchType.Local, cache);
			double msLocal = sw.Elapsed.TotalMilliseconds;
			var autoLocal = new HashSet<ConcernPoint>(local.Where(kv => kv.Value.FirstOrDefault()?.IsAuto ?? false).Select(kv => kv.Key));
			var rest = points.Where(p => !autoLocal.Contains(p)).ToList();
			sw.Restart();
			var global = rest.Count > 0
				? cf.FindPoints(rest, newFiles, ContextFinder.SearchType.Global, cache)
				: new Dictionary<ConcernPoint, List<RemapCandidateInfo>>();
			double msGlobal = sw.Elapsed.TotalMilliseconds;

			var result = new Dictionary<ConcernPoint, (List<RemapCandidateInfo> cands, string search)>();
			foreach (var kv in local) result[kv.Key] = (kv.Value, "local");
			foreach (var kv in global) result[kv.Key] = (kv.Value, "global");

			int nAutoTotal = 0;
			foreach (var p in points)
			{
				pointId.TryGetValue(p, out var oid);
				if (!result.TryGetValue(p, out var r))
				{
					pqRows.Add(Csv(label, oid ?? "", p.Context.Type, "none", "", "", "", "", "", 0, 0));
					continue;
				}
				var cands = r.cands.Where(c => !c.Deleted).ToList();
				var first = cands.FirstOrDefault();
				var second = cands.Count > 1 ? cands[1] : null;
				string top1Id = "", top1File = "", top1Off = "";
				if (first != null)
				{
					top1File = first.File?.Name ?? first.Context?.FileName ?? "";
					int off = first.Node?.Location?.Start?.Offset ?? first.Context.StartOffset;
					top1Off = off.ToString(CultureInfo.InvariantCulture);
					newIds.TryGetValue((top1File, off), out top1Id);
					top1Id = top1Id ?? "";
				}
				bool isAuto = first?.IsAuto ?? false;
				if (isAuto) nAutoTotal++;
				pqRows.Add(Csv(label, oid ?? "", p.Context.Type, r.search, top1Id, top1File, top1Off,
					first?.Similarity ?? double.NaN, second?.Similarity ?? double.NaN, isAuto ? 1 : 0, cands.Count));
			}
			pairRows.Add(Csv(label, oldFiles.Count, newFiles.Count, points.Count, pointId.Count, autoLocal.Count, nAutoTotal,
				msParseOld, msParseNew, msAddLand, msLocal, msGlobal, msLocal + msGlobal, ""));
			Console.Error.WriteLine($"[cfbench] {label}: points={points.Count} (ids {pointId.Count}) auto={nAutoTotal} addland={msAddLand:F0}ms local={msLocal:F0}ms global={msGlobal:F0}ms");
		}

		static List<ParsedFile> ParseDir(BaseParser parser, string dir)
		{
			var files = Directory.GetFiles(dir, "*.graphql", SearchOption.AllDirectories)
				.OrderBy(p => p, StringComparer.Ordinal).ToList();
			var res = new List<ParsedFile>();
			foreach (var path in files)
			{
				var text = File.ReadAllText(path, Encoding.UTF8);
				var rel = path.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');
				Node root;
				lock (parser) { root = parser.Parse(text).Item1; }   // Parse возвращает (Node, Durations)
				if (root == null) throw new InvalidOperationException("parse failed: " + rel);
				res.Add(new ParsedFile { Name = rel, Text = text, Root = root });
			}
			return res;
		}

		/// (относительный путь файла, StartOffset узла поля) → Id якоря корпуса
		static Dictionary<(string, int), string> LoadAnchorIds(string path)
		{
			var d = new Dictionary<(string, int), string>();
			foreach (var o in JArray.Parse(File.ReadAllText(path, Encoding.UTF8)).OfType<JObject>())
			{
				// в дампе версий (e03/02_extract_versions.py) файлам не с расширением .graphql (schema.gql, *.graphqls)
				// добавлен суффикс .graphql, а в anchors/*.json записан исходный путь — храним ключ и с суффиксом
				var file = o["File"].ToString().Replace('\\', '/');
				var off = (int)o["StartOffset"];
				var id = o["Id"].ToString();
				if (!d.ContainsKey((file, off))) d[(file, off)] = id;
				if (!file.EndsWith(".graphql", StringComparison.OrdinalIgnoreCase) && !d.ContainsKey((file + ".graphql", off)))
					d[(file + ".graphql", off)] = id;
			}
			return d;
		}

		static Dictionary<string, string> ParseArgs(IEnumerable<string> a)
		{
			var d = new Dictionary<string, string>(StringComparer.Ordinal);
			string key = null;
			foreach (var s in a)
			{
				if (s.StartsWith("--")) { key = s.Substring(2); d[key] = "true"; }
				else if (key != null) { d[key] = s; key = null; }
			}
			return d;
		}

		static string Csv(params object[] cells)
		{
			var sb = new StringBuilder();
			for (int i = 0; i < cells.Length; i++)
			{
				if (i > 0) sb.Append(',');
				var c = cells[i];
				string s = c is double dd ? dd.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(c, CultureInfo.InvariantCulture) ?? "";
				if (s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
				sb.Append(s);
			}
			return sb.ToString();
		}

		static void WriteCsv(string path, string header, List<string> rows)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
			File.WriteAllLines(path, new[] { header }.Concat(rows), new UTF8Encoding(false));
		}
	}
}
