using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Локальное измерение для markup/experiments/e13_storage_size (§2б): реальные файлы сервера в варианте C —
/// .land/anchors.json (версия 10, без мешков) и .land/vp_index.json (кэш снимков деревьев) — на схемах пробы e13
/// (каталоги experiments/e13_storage_size/_work/files/*, по одному .graphql в каждом). Пропускается, если каталогов нет.
/// Результат: results/server_store_sizes.csv в каталоге серии.
/// </summary>
[TestClass]
public class E13ServerStoreSizesTests
{
	private static string GrammarsFolder => Path.Combine(AppContext.BaseDirectory, "grammars");
	private const string E13 = @"E:\phd\my\for_plugin\markup\experiments\e13_storage_size";
	private static readonly string[] Files = { "authz", "insights", "batches", "wasmer", "schema" };

	private string _tempFolder;

	public TestContext TestContext { get; set; }

	[TestInitialize]
	public void Setup()
	{
		_tempFolder = Path.Combine(Path.GetTempPath(), "markup-tests-e13-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempFolder);
	}

	[TestCleanup]
	public void Cleanup()
	{
		try
		{
			if (!string.IsNullOrEmpty(_tempFolder) && Directory.Exists(_tempFolder))
				Directory.Delete(_tempFolder, true);
		}
		catch
		{
		}
	}

	[TestMethod]
	[TestCategory("Local")]
	public async Task LocalE13_ServerStoreSizes_VariantC()
	{
		var src = Path.Combine(E13, "_work", "files");
		if (!Directory.Exists(src))
			Assert.Inconclusive("нет каталога " + src);

		var rows = new List<string> { "label,n_anchors_total,n_gql_fields,anchors_json_bytes,vp_index_bytes,total_bytes,ms_list_anchors,ms_forests,forest_source" };
		foreach (var name in Files)
		{
			var from = Path.Combine(src, name);
			if (!Directory.Exists(from))
				continue;

			var folder = Path.Combine(_tempFolder, name);
			Directory.CreateDirectory(folder);
			foreach (var f in Directory.GetFiles(from, "*.graphql"))
				File.Copy(f, Path.Combine(folder, Path.GetFileName(f)), true);

			var svc = new LandService();
			await svc.InitializeAsync(new InitializeParams
			{
				protocolVersion = 2,
				offsetEncoding = "utf-16",
				graphqlParserPath = Path.Combine(GrammarsFolder, "graphql.land"),
				typescriptParserPath = Path.Combine(GrammarsFolder, "ts_resolvers.land"),
			});

			var sw = System.Diagnostics.Stopwatch.StartNew();
			await svc.ListAnchorsAsync(new ListTreeParams { folderPath = folder, forceRescan = true, preferCache = false });
			var msList = sw.Elapsed.TotalMilliseconds;
			sw.Restart();
			svc.BuildCurrentRebindingForestsFromDisk();   // строит деревья и пишет кэш снимков
			var msForests = sw.Elapsed.TotalMilliseconds;

			var anchorsPath = AnchorStore.GetStorePath(folder);
			var treePath = TreeCacheStore.GetPath(folder);
			Assert.IsTrue(File.Exists(anchorsPath), name + ": anchors.json");
			Assert.IsTrue(File.Exists(treePath), name + ": vp_index.json");
			Assert.IsFalse(File.ReadAllText(anchorsPath).Contains("\"NeighborBag\"", StringComparison.Ordinal));

			var all = svc.CurrentMarkupForTests.Anchors.Count;
			var fields = svc.CurrentMarkupForTests.Anchors.Count(a => string.Equals(a.AnchorKind, "gqlField", StringComparison.OrdinalIgnoreCase));
			var ab = new FileInfo(anchorsPath).Length;
			var tb = new FileInfo(treePath).Length;
			var source = string.Join("|", svc.LastForestSource.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
			var line = string.Join(",", name, all, fields, ab, tb, ab + tb, msList.ToString("F0", CultureInfo.InvariantCulture), msForests.ToString("F0", CultureInfo.InvariantCulture), "\"" + source + "\"");
			rows.Add(line);
			TestContext.WriteLine(line);
		}

		Assert.IsTrue(rows.Count > 1, "ни одной схемы не измерено");
		var resultsDir = Path.Combine(E13, "results");
		if (Directory.Exists(resultsDir))
			File.WriteAllLines(Path.Combine(resultsDir, "server_store_sizes.csv"), rows, new UTF8Encoding(false));
	}
}
