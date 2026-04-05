using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MarkupServer
{
	public partial class LandService
	{
		private static DeleteAnchorResultV2 MakeDeleteAnchorResult(bool? deleted, string message = null)
			=> new DeleteAnchorResultV2 { d = deleted, m = message };

		private static string NormalizeSuppressFilePath(string filePath)
		{
			var normalized = Path.GetFullPath(filePath ?? string.Empty).Replace('\\', '/');
			return normalized.ToLowerInvariant();
		}

		private static string BuildSuppressKey(TreeNode node)
		{
			if (node == null || !string.Equals(node.NodeType, "anchor", StringComparison.OrdinalIgnoreCase))
				return null;

			var language = (node.Language ?? string.Empty).Trim().ToLowerInvariant();
			var kind = (node.AnchorKind ?? string.Empty).Trim();
			var file = NormalizeSuppressFilePath(node.Filepath);

			if (string.Equals(language, LangGql, StringComparison.OrdinalIgnoreCase))
			{
				if (string.Equals(kind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase))
					return $"{language}|{kind}|{file}|{node.GqlTypeKind ?? "type"}|{node.ParentNameRaw ?? string.Empty}|{node.MethodNameNorm ?? string.Empty}";

				if (IsGqlTypeContainerKind(kind))
					return $"{language}|{kind}|{file}|{node.GqlTypeKind ?? "type"}|{node.MethodNameNorm ?? string.Empty}";
			}

			if (string.Equals(language, LangTs, StringComparison.OrdinalIgnoreCase))
				return $"{language}|{kind}|{file}|{node.ParentNameNorm ?? string.Empty}|{node.MethodNameNorm ?? string.Empty}|{node.ReturnTypeNorm ?? string.Empty}";

			return $"{language}|{kind}|{file}|{node.StartOffset ?? -1}|{node.EndOffset ?? -1}";
		}

		private static HashSet<string> GetSuppressedKeysSet(PersistedMarkup markup)
			=> new HashSet<string>(markup?.SuppressedAnchorKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

		private bool IsSuppressed(TreeNode node, HashSet<string> suppressed)
		{
			if (node == null || suppressed == null || suppressed.Count == 0)
				return false;

			var key = BuildSuppressKey(node);
			return !string.IsNullOrWhiteSpace(key) && suppressed.Contains(key);
		}

		private void RemoveSuppressionForNodeUnsafe(TreeNode node)
		{
			if (node == null)
				return;

			currentMarkup ??= new PersistedMarkup();
			currentMarkup.SuppressedAnchorKeys ??= new List<string>();
			var key = BuildSuppressKey(node);
			if (string.IsNullOrWhiteSpace(key))
				return;

			currentMarkup.SuppressedAnchorKeys = currentMarkup.SuppressedAnchorKeys
				.Where(x => !string.Equals(x ?? string.Empty, key, StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		private void AddSuppressionForNodeUnsafe(TreeNode node)
		{
			if (node == null)
				return;

			currentMarkup ??= new PersistedMarkup();
			currentMarkup.SuppressedAnchorKeys ??= new List<string>();
			var key = BuildSuppressKey(node);
			if (string.IsNullOrWhiteSpace(key))
				return;

			if (!currentMarkup.SuppressedAnchorKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
				currentMarkup.SuppressedAnchorKeys.Add(key);
		}

		[JsonRpcMethod("land/deleteAnchor", UseSingleObjectParameterDeserialization = true)]
		public Task<DeleteAnchorResultV2> DeleteAnchorAsync(DeleteAnchorParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.anchorId))
				throw new ArgumentException("anchorId is required");

			if (!nodesById.TryGetValue(p.anchorId, out var oldNode) || oldNode == null)
			{
				Debug($"[deleteAnchor] not found anchor by id [{p.anchorId}] (cache lost). Please run land/listAnchors again.");
				return Task.FromResult(MakeDeleteAnchorResult(false, "Точка не найдена в текущем снимке. Обновите панель."));
			}

			var effectiveNode = ResolveEffectiveAnchorForParentRebind(oldNode) ?? CloneAnchor(oldNode);
			effectiveNode.Id = oldNode.Id;
			effectiveNode.IsManual = oldNode.IsManual;
			effectiveNode.AnchorFamily = EnsureAnchorFamily(effectiveNode);

			lock (markupLock)
			{
				currentMarkup ??= new PersistedMarkup();
				currentMarkup.Anchors ??= new List<TreeNode>();
				currentMarkup.SuppressedAnchorKeys ??= new List<string>();

				currentMarkup.Anchors = currentMarkup.Anchors
					.Where(x => !string.Equals(x?.Id, p.anchorId, StringComparison.OrdinalIgnoreCase))
					.ToList();
				AddSuppressionForNodeUnsafe(effectiveNode);
				RebuildRelationsUnsafe();
				RebuildMarkupRootsUnsafe();
				ReloadNodesByIdUnsafe();
				if (!AnchorStore.TrySave(currentFolderPath, currentMarkup, out var err))
					Debug($"[deleteAnchor] cannot save anchors cache: {err}");
			}

			Debug($"[deleteAnchor] deleted anchor {p.anchorId}; suppressed={BuildSuppressKey(effectiveNode)}");
			return Task.FromResult(MakeDeleteAnchorResult(true, "Точка удалена из разметки."));
		}
	}
}
