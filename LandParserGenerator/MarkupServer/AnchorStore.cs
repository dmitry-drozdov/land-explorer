using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MarkupServer
{
	/// <summary>
	/// Персистентное хранение semantic snapshot разметки.
	/// Храним anchors + relations + materialized Roots для быстрого старта.
	/// VP-дерево не сохраняем.
	/// </summary>
	internal static class AnchorStore
	{
		public const int CurrentVersion = 6;
		private const string FolderName = ".land";
		private const string FileName = "anchors.json";

		public static string GetStorePath(string folderPath)
		{
			folderPath = Path.GetFullPath(folderPath);
			return Path.Combine(folderPath, FolderName, FileName);
		}

		public static bool TryLoad(string folderPath, out PersistedMarkup data, out string error)
		{
			error = null;
			data = null;
			try
			{
				var path = GetStorePath(folderPath);
				if (!File.Exists(path))
					return false;

				var json = File.ReadAllText(path, Encoding.UTF8);
				if (string.IsNullOrWhiteSpace(json))
				{
					error = "file is empty";
					return false;
				}

				var jo = JObject.Parse(json);
				var version = jo.Value<int?>(nameof(PersistedMarkup.Version)) ?? 0;

				if (version == CurrentVersion)
				{
					var parsed = jo.ToObject<PersistedMarkup>();
					if (parsed == null)
					{
						error = "file is invalid json";
						return false;
					}

					parsed.Anchors ??= DeriveAnchorsFromRoots(parsed.Roots);
					foreach (var anchor in parsed.Anchors)
						EnsureAnchorSemantics(anchor);
					parsed.Relations ??= DeriveRelationsFromRoots(parsed.Roots);
					parsed.Roots ??= new List<TreeNode>();
					parsed.SuppressedAnchorKeys ??= new List<string>();
					data = parsed;
					return true;
				}

				if (version == 2 || version == 3 || version == 4 || version == 5)
				{
					var legacy = jo.ToObject<PersistedMarkup>();
					if (legacy == null)
					{
						error = "file is invalid json";
						return false;
					}

					var roots = legacy.Roots ?? new List<TreeNode>();
					var anchors = legacy.Anchors ?? DeriveAnchorsFromRoots(roots);
					foreach (var anchor in anchors)
						EnsureAnchorSemantics(anchor);

					data = new PersistedMarkup
					{
						Version = CurrentVersion,
						FolderPath = legacy.FolderPath,
						UpdatedAtUtc = legacy.UpdatedAtUtc,
						Roots = roots,
						Anchors = anchors,
						Relations = legacy.Relations ?? DeriveRelationsFromRoots(roots),
						SuppressedAnchorKeys = legacy.SuppressedAnchorKeys ?? new List<string>(),
					};
					return true;
				}

				error = $"unsupported cache version {version} (expected {CurrentVersion})";
				return false;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				data = null;
				return false;
			}
		}

		public static bool TrySave(string folderPath, PersistedMarkup data, out string error)
		{
			error = null;
			try
			{
				folderPath = Path.GetFullPath(folderPath);
				var path = GetStorePath(folderPath);
				var dir = Path.GetDirectoryName(path);
				Directory.CreateDirectory(dir);

				data.Version = CurrentVersion;
				data.FolderPath = folderPath;
				data.UpdatedAtUtc = DateTime.UtcNow;
				data.Anchors ??= new List<TreeNode>();
				foreach (var anchor in data.Anchors)
					EnsureAnchorSemantics(anchor);
				data.Relations ??= new List<PersistedRelation>();
				data.Roots ??= new List<TreeNode>();
				data.SuppressedAnchorKeys ??= new List<string>();

				var json = JsonConvert.SerializeObject(
					data,
					Formatting.Indented,
					new JsonSerializerSettings
					{
						NullValueHandling = NullValueHandling.Ignore
					});

				var tmp = path + ".tmp";
				File.WriteAllText(tmp, json, Encoding.UTF8);
				if (File.Exists(path))
					File.Delete(path);
				File.Move(tmp, path);
				return true;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				return false;
			}
		}

		private static void EnsureAnchorSemantics(TreeNode anchor)
		{
			if (anchor == null)
				return;

			if (string.IsNullOrWhiteSpace(anchor.Language))
			{
				if ((anchor.Id ?? "").StartsWith("ts:", StringComparison.OrdinalIgnoreCase))
				{
					anchor.Language = "ts";
					anchor.AnchorKind = string.IsNullOrWhiteSpace(anchor.AnchorKind) ? "tsMember" : anchor.AnchorKind;
				}
				else
				{
					anchor.Language = "gql";
					anchor.AnchorKind = string.IsNullOrWhiteSpace(anchor.AnchorKind) ? "gqlField" : anchor.AnchorKind;
				}
			}

			if (string.IsNullOrWhiteSpace(anchor.AnchorFamily))
			{
				anchor.AnchorFamily = (anchor.AnchorKind ?? "") switch
				{
					"gqlTypeDef" => "recordLike",
					"gqlInputDef" => "recordLike",
					"gqlInterfaceDef" => "recordLike",
					_ => "callableMember",
				};
			}
		}

		public static IEnumerable<TreeNode> EnumerateNodes(IEnumerable<TreeNode> roots)
		{
			if (roots == null) yield break;
			var stack = new Stack<TreeNode>(roots.Reverse());
			while (stack.Count > 0)
			{
				var n = stack.Pop();
				if (n == null) continue;
				yield return n;
				if (n.Children != null)
				{
					for (int i = n.Children.Count - 1; i >= 0; i--)
						stack.Push(n.Children[i]);
				}
			}
		}

		private static List<TreeNode> DeriveAnchorsFromRoots(List<TreeNode> roots)
		{
			var anchors = EnumerateNodes(roots)
				.Where(x => x?.NodeType == "anchor" && !string.IsNullOrWhiteSpace(x.Id))
				.Select(CloneAnchor)
				.ToList();

			foreach (var anchor in anchors)
				EnsureAnchorSemantics(anchor);

			return anchors;
		}

		private static List<PersistedRelation> DeriveRelationsFromRoots(List<TreeNode> roots)
		{
			var relations = new List<PersistedRelation>();
			foreach (var group in EnumerateNodes(roots).Where(x => x?.NodeType == "group" && (x.Id ?? "").StartsWith("gqlField:", StringComparison.OrdinalIgnoreCase)))
			{
				var gql = group.Children?.FirstOrDefault(x => x?.NodeType == "anchor" && (x.Id ?? "").StartsWith("gql:", StringComparison.OrdinalIgnoreCase));
				if (gql == null)
					continue;

				var targets = group.Children
					.Where(x => x?.NodeType == "anchor" && (x.Id ?? "").StartsWith("ts:", StringComparison.OrdinalIgnoreCase))
					.Select(x => x.Id)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();

				if (targets.Count == 0)
					continue;

				relations.Add(new PersistedRelation
				{
					SourceAnchorId = gql.Id,
					TargetAnchorIds = targets,
				});
			}

			return relations;
		}

		private static TreeNode CloneAnchor(TreeNode n)
		{
			if (n == null)
				return null;

			return new TreeNode
			{
				Id = n.Id,
				Name = n.Name,
				NodeType = n.NodeType,
				IsManual = n.IsManual,
				Filepath = n.Filepath,
				StartOffset = n.StartOffset,
				EndOffset = n.EndOffset,
				Language = n.Language,
				AnchorKind = n.AnchorKind,
				AnchorFamily = n.AnchorFamily,
				GqlTypeKind = n.GqlTypeKind,
				MethodNameNorm = n.MethodNameNorm,
				ParentNameNorm = n.ParentNameNorm,
				ParentNameRaw = n.ParentNameRaw,
				ReturnTypeNorm = n.ReturnTypeNorm,
				Args = n.Args?.Select(x => new Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm }).ToList(),
				OrdinalInParent = n.OrdinalInParent,
				NeighborBag = n.NeighborBag != null ? new Dictionary<string, double>(n.NeighborBag, StringComparer.Ordinal) : null,
			};
		}
	}

	internal sealed class PersistedRelation
	{
		public string SourceAnchorId { get; set; }
		public List<string> TargetAnchorIds { get; set; } = new();
	}

	internal sealed class PersistedMarkup
	{
		public int Version { get; set; } = AnchorStore.CurrentVersion;
		public string FolderPath { get; set; }
		public DateTime UpdatedAtUtc { get; set; }
		public List<TreeNode> Roots { get; set; } = new();
		public List<TreeNode> Anchors { get; set; } = new();
		public List<PersistedRelation> Relations { get; set; } = new();
		public List<string> SuppressedAnchorKeys { get; set; } = new();
	}
}
