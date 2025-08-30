using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VPTree;

namespace Tests
{
	[TestClass]
	public class RebinderTests
	{
		[TestMethod]
		public void Query_Knn_Smoke_RoundTripJsonAndOrderedDistances()
		{
			// используем временный файл вместо "anchors.json"
			var jsonPath = Path.Combine(
			    Path.GetTempPath(),
			    "anchors_" + Guid.NewGuid().ToString("N") + ".json");

			try
			{
				// Arrange
				var anchors = new List<MethodAnchor>
				{
				    MethodAnchor.FromRaw("old#1",  0, 100,"ComputeHash",
					new[] { Tuple.Create("int","a"), Tuple.Create("string","b") },
					new[] { "int" }, "Hasher"),

				    MethodAnchor.FromRaw("old#2", 0, 100, "FindUser",
					new[] { Tuple.Create("int","id") },
					new[] { "*User", "error" }, "Repo"),

				    MethodAnchor.FromRaw("old#3", 0, 100, "WriteJSON",
					new[] { Tuple.Create("io.Writer","w"), Tuple.Create("any","v"), Tuple.Create("bool","indent") },
					new[] { "error" }, "Encoder"),
				};

				AnchorsIO.SaveJson(jsonPath, anchors);
				Assert.IsTrue(File.Exists(jsonPath), "JSON-файл с якорями не был создан.");

				var loaded = AnchorsIO.LoadJson(jsonPath);
				Assert.IsNotNull(loaded, "AnchorsIO.LoadJson вернул null.");
				Assert.AreEqual(anchors.Count, loaded.Count, "Количество загруженных якорей не совпадает.");

				// сверим набор Id (порядок не важен)
				CollectionAssert.AreEquivalent(
				    anchors.Select(a => a.Id).ToList(),
				    loaded.Select(a => a.Id).ToList(),
				    "Набор Id якорей после загрузки отличается.");

				var weights = new Dist.Weights();
				var rebinder = new Rebinder(loaded, weights);

				var q = MethodAnchor.FromRaw("new#tmp", 0, 100, "FindUser",
				    new[] { Tuple.Create("int", "a"), Tuple.Create("string", "b") },
				    new[] { "int" }, "Hasher");

				// Act
				var knn = rebinder.Query(q, 3);

				// Assert
				Assert.IsNotNull(knn, "Query вернул null.");
				Assert.IsTrue(knn.Count > 0, "Query вернул пустой список.");
				Assert.IsTrue(knn.Count <= 3, "Query вернул больше элементов, чем k.");

				// индексы валидны, расстояния неотрицательны и отсортированы по возрастанию
				for (int i = 0; i < knn.Count; i++)
				{
					var k = knn[i];
					Assert.IsTrue(k.Index >= 0 && k.Index < loaded.Count, "Некорректный индекс кандидата.");
					Assert.IsTrue(k.Dist >= 0.0, "Дистанция должна быть неотрицательной.");

					if (i > 0)
					{
						Assert.IsTrue(knn[i - 1].Dist <= k.Dist, "Список кандидатов должен быть отсортирован по возрастанию дистанции.");
					}
				}

				// Дополнительно можно вывести в тестовый вывод (необязательно)
				for (int i = 0; i < knn.Count; i++)
				{
					var k = knn[i];
					var cand = loaded[k.Index];
					Console.WriteLine(string.Format("#{0}: {1}  {2}  dist={3:0.0000}",
					    i + 1, cand.Id, cand.MethodNameNorm, k.Dist));
				}
			}
			finally
			{
				// cleanup
				try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch { /* ignore */ }
			}
		}
	}

	[TestClass]
	public class RebinderKnnTableTests
	{
		// helper: веса с нулями по умолчанию, чтобы изолировать компоненты
		private static Dist.Weights W(double nameW = 0, double argsW = 0, double returnsW = 0, double recvW = 0)
		{
			return new Dist.Weights
			{
				NameW = nameW,
				ArgsW = argsW,
				ReturnsW = returnsW,
				ParentW = recvW,
			};
		}

		// helper: удобнее писать аргументы без ValueTuple
		private static Tuple<string, string> Arg(string type, string name) => Tuple.Create(type, name);

		private static MethodAnchor A(
		    string id, string name,
		    Tuple<string, string>[] args,
		    string[] rets,
		    string recv)
		{
			return MethodAnchor.FromRaw(id, 0, 100, name, args ?? new Tuple<string, string>[0], rets ?? new string[0], recv);
		}

		#region 1) Имя — Levenshtein, изолировано NameW
		[DataTestMethod]
		[TestCategory("KNN/Name")]
		[DataRow("FindUsr", "find user")]
		[DataRow("WriteJSN", "write json")]
		public void Nearest_ByName_Levenshtein(string queryName, string expectedBestName)
		{
			var corpus = new List<MethodAnchor>
			    {
				A("N#1", "FindUser",    new[] { Arg("int","id") },           new[] { "*User","error" }, "Repo"),
				A("N#2", "ComputeHash", new[] { Arg("int","a"), Arg("string","b") }, new[] { "int" }, "Hasher"),
				A("N#3", "WriteJSON",   new[] { Arg("io.Writer","w"), Arg("any","v") }, new[] { "error" }, "Encoder"),
			    };

			var q = A("Q#name", queryName, new[] { Arg("int", "x") }, new[] { "error" }, "Repo");

			var rebinder = new Rebinder(corpus, W(nameW: 1.0));
			var knn = rebinder.Query(q, 3);

			var best = corpus[knn[0].Index];
			Assert.AreEqual(expectedBestName, best.MethodNameNorm, "Ожидался ближайший по имени.");
			for (int i = 1; i < knn.Count; i++) Assert.IsTrue(knn[i - 1].Dist <= knn[i].Dist);
		}
		#endregion

		#region 2) Аргументы — permutation-invariant (Hungarian), изолировано ArgsW
		[TestMethod]
		[TestCategory("KNN/Args")]
		public void Nearest_ByArgs_PermutationInvariant()
		{
			var corpus = new List<MethodAnchor>
			    {
				// тот же порядок
				A("A#1", "Add", new[] { Arg("int","a"), Arg("string","b") }, new[] { "int" }, "Math"),
				// переставлены местами
				A("A#2", "Add", new[] { Arg("string","b"), Arg("int","a") }, new[] { "int" }, "Math"),
				// другой набор типов
				A("A#3", "Add", new[] { Arg("int","a"), Arg("int","b") }, new[] { "int" }, "Math"),
			    };

			var q = A("Q#args", "Add", new[] { Arg("int", "x"), Arg("string", "y") }, new[] { "int" }, "Math");

			var rebinder = new Rebinder(corpus, W(argsW: 1.0));
			var knn = rebinder.Query(q, 3);

			var top2 = knn.Take(2).Select(k => corpus[k.Index].Id).ToList();
			CollectionAssert.AreEquivalent(new[] { "A#1", "A#2" }, top2,
			    "Первые два соседа должны совпадать по типам (порядок не важен).");

			Assert.AreEqual(knn[0].Dist, knn[1].Dist, 1e-9, "Перестановка аргументов не должна менять расстояние.");
			Assert.IsTrue(knn[1].Dist <= knn[2].Dist, "Сигнатура с иными типами должна быть дальше.");
		}
		#endregion

		#region 3) Возвращаемые типы — изолировано ReturnsW
		[TestMethod]
		[TestCategory("KNN/Returns")]
		public void Nearest_ByReturns_PrefersExactReturnType()
		{
			var corpus = new List<MethodAnchor>
			    {
				A("R#1", "GetCount", new Tuple<string,string>[0], new[] { "int" },  "Repo"),
				A("R#2", "GetCount", new Tuple<string,string>[0], new[] { "long" }, "Repo"),
				A("R#3", "GetUser",  new[] { Arg("int","id") },  new[] { "*User","error" }, "Repo"),
			    };

			var q = A("Q#ret", "GetCount", new Tuple<string, string>[0], new[] { "int" }, "Repo");

			var rebinder = new Rebinder(corpus, W(returnsW: 1.0));
			var knn = rebinder.Query(q, 3);

			var best = corpus[knn[0].Index];
			Assert.AreEqual("R#1", best.Id, "Точное совпадение возвращаемого типа должно быть ближе всего.");
		}
		#endregion

		#region 4) Ресивер — изолировано ReceiverW
		[DataTestMethod]
		[TestCategory("KNN/Receiver")]
		[DataRow("Hasher", "RCV#1")]
		[DataRow("Repo", "RCV#2")]
		public void Nearest_ByReceiver_PrefersMatchingReceiver(string recv, string expectedBestId)
		{
			var corpus = new List<MethodAnchor>
			    {
				A("RCV#1", "ComputeHash", new[] { Arg("int","a"), Arg("string","b") }, new[] { "int" }, "Hasher"),
				A("RCV#2", "ComputeHash", new[] { Arg("int","a"), Arg("string","b") }, new[] { "int" }, "Repo"),
			    };

			var q = A("Q#recv", "ComputeHash", new[] { Arg("int", "a"), Arg("string", "b") }, new[] { "int" }, recv);

			var rebinder = new Rebinder(corpus, W(recvW: 1.0));
			var knn = rebinder.Query(q, 2);

			var best = corpus[knn[0].Index];
			Assert.AreEqual(expectedBestId, best.Id);
			Assert.IsTrue(knn[0].Dist <= knn[1].Dist);
		}
		#endregion

		#region 5) Смешанный случай — дефолтные веса Dist.Weights
		[TestMethod]
		[TestCategory("KNN/Mixed")]
		public void Nearest_Mixed_DefaultWeights()
		{
			var corpus = new List<MethodAnchor>
			    {
				A("old#1", "ComputeHash",
				    new[] { Arg("int","a"), Arg("string","b") },
				    new[] { "int" }, "Hasher"),

				A("old#2", "FindUser",
				    new[] { Arg("int","id") },
				    new[] { "*User", "error" }, "Repo"),

				A("old#3", "WriteJSON",
				    new[] { Arg("io.Writer","w"), Arg("any","v"), Arg("bool","indent") },
				    new[] { "error" }, "Encoder"),
			    };

			var q = A("new#tmp", "FindUser",
			    new[] { Arg("int", "a"), Arg("string", "b") },
			    new[] { "int" }, "Hasher");

			var rebinder = new Rebinder(corpus, new Dist.Weights()); // дефолтные веса

			var knn = rebinder.Query(q, 3);
			var best = corpus[knn[0].Index];

			Assert.AreEqual("old#1", best.Id, "Ожидался ближайший по сумме признаков (args/returns/receiver).");
			for (int i = 1; i < knn.Count; i++) Assert.IsTrue(knn[i - 1].Dist <= knn[i].Dist);
		}
		#endregion
	}
}
