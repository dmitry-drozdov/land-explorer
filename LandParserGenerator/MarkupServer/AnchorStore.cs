using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MarkupServer
{
	/// <summary>
	/// Персистентное хранение разметки (дерева TreeNode) для конкретного folderPath.
	/// Храним ТОЛЬКО «снимок разметки» (Roots + якоря), VP-дерево не сохраняем.
	/// </summary>
	internal static class AnchorStore
	{
		public const int CurrentVersion = 1;
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
				var parsed = JsonConvert.DeserializeObject<PersistedMarkup>(json);
				if (parsed == null)
				{
					error = "file is empty or invalid json";
					return false;
				}

				if (parsed.Version != CurrentVersion)
				{
					error = $"unsupported cache version {parsed.Version} (expected {CurrentVersion})";
					return false;
				}

				data = parsed;
				return true;
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

				var json = JsonConvert.SerializeObject(
					data,
					Formatting.Indented,
					new JsonSerializerSettings
					{
						NullValueHandling = NullValueHandling.Ignore
					}
				);

				// tmp + move, чтобы не оставлять полупустой файл при падении.
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

		public static bool ReplaceNodeInTree(List<TreeNode> roots, TreeNode updated)
		{
			if (roots == null || updated == null || string.IsNullOrWhiteSpace(updated.Id))
				return false;

			bool ReplaceIn(List<TreeNode> nodes)
			{
				for (int i = 0; i < nodes.Count; i++)
				{
					var cur = nodes[i];
					if (cur != null && cur.Id == updated.Id)
					{
						// сохраняем Children, если сервер вернул узел без Children
						updated.Children = updated.Children ?? cur.Children;
						nodes[i] = updated;
						return true;
					}

					if (cur?.Children != null && ReplaceIn(cur.Children))
						return true;
				}
				return false;
			}

			return ReplaceIn(roots);
		}
	}

	internal sealed class PersistedMarkup
	{
		public int Version { get; set; } = AnchorStore.CurrentVersion;
		public string FolderPath { get; set; }
		public DateTime UpdatedAtUtc { get; set; }
		public List<TreeNode> Roots { get; set; } = new();
	}
}
