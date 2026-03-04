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
		public const int CurrentVersion = 2;
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

		/// <summary>
		/// Обновляет anchor-узел в дереве и, при необходимости, переносит его между группами.
		/// Используется в updateAnchor, потому что якорь может "переехать" в другой GraphQL-тип (группу).
		/// </summary>
		public static void UpsertAnchorInTree(
			List<TreeNode> roots,
			TreeNode updated,
			string targetTypeGroupId,
			string targetTypeGroupName,
			string targetFieldGroupId,
			string targetFieldGroupName)
		{
			if (roots == null || updated == null || string.IsNullOrWhiteSpace(updated.Id))
				return;

			// 1) Удаляем старую версию узла из дерева (если она там есть)
			TreeNode oldTypeGroup = null;
			TreeNode oldFieldGroup = null;
			bool RemoveIn(List<TreeNode> nodes, TreeNode currentTypeGroup, TreeNode currentFieldGroup)
			{
				for (int i = 0; i < nodes.Count; i++)
				{
					var cur = nodes[i];
					if (cur != null && cur.Id == updated.Id)
					{
						oldTypeGroup = currentTypeGroup;
						oldFieldGroup = currentFieldGroup;
						nodes.RemoveAt(i);
						return true;
					}

					if (cur?.Children != null && cur.Children.Count > 0)
					{
						var nextType = currentTypeGroup;
						var nextField = currentFieldGroup;
						if (string.Equals(cur.NodeType, "group", StringComparison.OrdinalIgnoreCase))
						{
							if ((cur.Id ?? "").StartsWith("gqlType:", StringComparison.OrdinalIgnoreCase))
								nextType = cur;
							if ((cur.Id ?? "").StartsWith("gqlField:", StringComparison.OrdinalIgnoreCase))
								nextField = cur;
						}
						if (RemoveIn(cur.Children, nextType, nextField))
							return true;
					}
				}
				return false;
			}

			RemoveIn(roots, null, null);

			// 2) Находим/создаём целевую type-группу. Если она не задана — просто пытаемся заменить/добавить как root.
			if (string.IsNullOrWhiteSpace(targetTypeGroupId))
			{
				if (!ReplaceNodeInTree(roots, updated))
					roots.Add(updated);
				return;
			}

			var typeGroup = roots.FirstOrDefault(x => x != null
				&& string.Equals(x.NodeType, "group", StringComparison.OrdinalIgnoreCase)
				&& x.Id == targetTypeGroupId);

			if (typeGroup == null)
			{
				typeGroup = new TreeNode
				{
					Id = targetTypeGroupId,
					Name = targetTypeGroupName,
					NodeType = "group",
					Children = new List<TreeNode>(),
				};
				roots.Add(typeGroup);
			}
			else
			{
				// если группа существует, но Name пустое — заполним
				if (string.IsNullOrWhiteSpace(typeGroup.Name) && !string.IsNullOrWhiteSpace(targetTypeGroupName))
					typeGroup.Name = targetTypeGroupName;
			}

			// 3) Находим/создаём целевую field-группу внутри type-группы
			typeGroup.Children ??= new List<TreeNode>();
			TreeNode fieldGroup = null;
			if (!string.IsNullOrWhiteSpace(targetFieldGroupId))
			{
				fieldGroup = typeGroup.Children.FirstOrDefault(x => x != null
					&& string.Equals(x.NodeType, "group", StringComparison.OrdinalIgnoreCase)
					&& x.Id == targetFieldGroupId);

				if (fieldGroup == null)
				{
					fieldGroup = new TreeNode
					{
						Id = targetFieldGroupId,
						Name = targetFieldGroupName,
						NodeType = "group",
						Children = new List<TreeNode>(),
					};
					typeGroup.Children.Add(fieldGroup);
				}
				else
				{
					if (string.IsNullOrWhiteSpace(fieldGroup.Name) && !string.IsNullOrWhiteSpace(targetFieldGroupName))
						fieldGroup.Name = targetFieldGroupName;
				}
			}

			// 4) Вставляем/обновляем anchor в field-группе (если есть), иначе прямо в type-группе.
			var targetContainer = fieldGroup?.Children ?? typeGroup.Children;
			if (fieldGroup != null)
				fieldGroup.Children ??= new List<TreeNode>();
			targetContainer.RemoveAll(x => x != null && x.Id == updated.Id);
			targetContainer.Add(updated);

			// 5) Если старая field-группа опустела — удаляем её, затем проверяем type-группу.
			if (oldFieldGroup != null
				&& string.Equals(oldFieldGroup.NodeType, "group", StringComparison.OrdinalIgnoreCase)
				&& (oldFieldGroup.Children == null || oldFieldGroup.Children.Count == 0)
				&& oldTypeGroup != null)
			{
				oldTypeGroup.Children?.RemoveAll(x => x != null && x.Id == oldFieldGroup.Id);
			}

			if (oldTypeGroup != null
				&& string.Equals(oldTypeGroup.NodeType, "group", StringComparison.OrdinalIgnoreCase)
				&& (oldTypeGroup.Children == null || oldTypeGroup.Children.Count == 0)
				&& !ReferenceEquals(oldTypeGroup, typeGroup))
			{
				roots.RemoveAll(x => x != null && x.Id == oldTypeGroup.Id);
			}
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
