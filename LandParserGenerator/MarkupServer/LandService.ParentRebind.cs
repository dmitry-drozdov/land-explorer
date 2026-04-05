using Land.Core.Parsing.Tree;
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
		private static ParentRebindOptionsResultV2 MakeParentRebindOptionsResult(
			TreeNode effective,
			IEnumerable<TreeNode> options,
			string message = null)
		{
			var effectiveClone = effective != null ? CloneAnchorStatic(effective) : null;
			var optionList = (options ?? Enumerable.Empty<TreeNode>())
				.Where(x => x != null)
				.Select(x => CloneAnchorStatic(x))
				.ToList();

			return new ParentRebindOptionsResultV2
			{
				e = ToClientNodeV2(effectiveClone),
				o = optionList
					.Select(x => new ParentRebindOptionV2
					{
						n = ToClientNodeV2(x),
						c = effectiveClone != null && IsSameTarget(x, effectiveClone),
					})
					.ToList(),
				m = message,
			};
		}

		private static TreeNode CloneAnchorStatic(TreeNode n)
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

		private static bool IsSameTarget(TreeNode a, TreeNode b)
		{
			if (a == null || b == null)
				return false;

			return string.Equals(Path.GetFullPath(a.Filepath ?? ""), Path.GetFullPath(b.Filepath ?? ""), StringComparison.OrdinalIgnoreCase)
				&& (a.StartOffset ?? -1) == (b.StartOffset ?? -1)
				&& (a.EndOffset ?? -1) == (b.EndOffset ?? -1)
				&& string.Equals(a.AnchorKind ?? "", b.AnchorKind ?? "", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsSameTarget(TreeNode node, ParentRebindTargetDescriptor target)
		{
			if (node == null || target == null)
				return false;

			return string.Equals(Path.GetFullPath(node.Filepath ?? ""), Path.GetFullPath(target.filePath ?? ""), StringComparison.OrdinalIgnoreCase)
				&& (node.StartOffset ?? -1) == (target.startOffset ?? -1)
				&& (node.EndOffset ?? -1) == (target.endOffset ?? -1)
				&& string.Equals(node.AnchorKind ?? "", target.anchorKind ?? "", StringComparison.OrdinalIgnoreCase);
		}

		private TreeNode ResolveEffectiveAnchorForParentRebind(TreeNode oldNode)
		{
			if (oldNode == null)
				return null;

			var effective = CloneAnchor(oldNode);
			effective.AnchorFamily = EnsureAnchorFamily(effective);

			var profileKey = GetProfileKey(effective);
			if (string.IsNullOrWhiteSpace(profileKey))
				return effective;

			var savedNodesById = new Dictionary<string, TreeNode>(nodesById, StringComparer.OrdinalIgnoreCase);
			try
			{
				BuildCurrentRebindingForestsFromDisk();
				var rebound = RebindAnchorAgainstCurrentForests(oldNode);
				if (rebound != null)
				{
					rebound.Id = oldNode.Id;
					rebound.IsManual = oldNode.IsManual;
					rebound.AnchorFamily = EnsureAnchorFamily(rebound);
					return rebound;
				}
			}
			catch (Exception ex)
			{
				Debug($"[parentRebind] cannot resolve effective anchor: {ex.Message}");
			}
			finally
			{
				nodesById = savedNodesById;
			}

			return effective;
		}

		private static bool ContainsRange(Node n, int startOffset, int endOffset)
		{
			if (n?.Location == null)
				return false;

			return n.Location.Start.Offset <= startOffset && endOffset <= n.Location.End.Offset;
		}

		private TreeNode TryGetImmediateGraphQlParentCandidate(TreeNode effectiveNode, out string message)
		{
			message = null;
			if (effectiveNode == null)
			{
				message = "Текущая точка не найдена.";
				return null;
			}

			if (!string.Equals(effectiveNode.Language, LangGql, StringComparison.OrdinalIgnoreCase))
			{
				message = "Перепривязка к родителю пока поддерживается только для GraphQL.";
				return null;
			}

			if (IsGqlTypeContainerKind(effectiveNode.AnchorKind))
			{
				message = "Для текущей точки GraphQL родитель не поддерживается.";
				return null;
			}

			if (!string.Equals(effectiveNode.AnchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase))
			{
				message = $"Перепривязка к родителю для kind='{effectiveNode.AnchorKind}' пока не поддерживается.";
				return null;
			}

			var filePath = Path.GetFullPath(effectiveNode.Filepath ?? "");
			if (!File.Exists(filePath))
			{
				message = $"Файл не найден: {filePath}";
				return null;
			}

			var sourceText = File.ReadAllText(filePath);
			var root = graphqlParser.Parse(sourceText).Item1;
			if (root == null)
			{
				message = "Не удалось распарсить GraphQL-файл.";
				return null;
			}

			var start = effectiveNode.StartOffset ?? -1;
			var end = effectiveNode.EndOffset ?? start;
			if (start < 0 || end < start)
			{
				message = "У текущей точки некорректный диапазон offsets.";
				return null;
			}

			foreach (var typeDef in GetGqlTypeDefs(root))
			{
				if (!ContainsRange(typeDef, start, end))
					continue;

				var gqlTypeKind = InferGqlTypeKind(typeDef);
				var parent = GetTreeNodeFromGqlTypeNode(typeDef, filePath, gqlTypeKind, sourceText);
				if (parent == null)
					continue;

				parent.IsManual = effectiveNode.IsManual;
				parent.AnchorFamily = EnsureAnchorFamily(parent);
				return parent;
			}

			message = "Не удалось определить родительский GraphQL type для текущей точки.";
			return null;
		}

		private List<TreeNode> BuildParentRebindOptions(TreeNode effectiveNode, out string message)
		{
			var options = new List<TreeNode>();
			message = null;

			if (effectiveNode == null)
			{
				message = "Текущая точка не найдена.";
				return options;
			}

			options.Add(CloneAnchor(effectiveNode));

			var parent = TryGetImmediateGraphQlParentCandidate(effectiveNode, out message);
			if (parent != null && !IsSameTarget(parent, effectiveNode))
				options.Add(parent);

			return options;
		}

		[JsonRpcMethod("land/getParentRebindOptions", UseSingleObjectParameterDeserialization = true)]
		public Task<ParentRebindOptionsResultV2> GetParentRebindOptionsAsync(ParentRebindOptionsParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.anchorId))
				throw new ArgumentException("anchorId is required");

			if (!nodesById.TryGetValue(p.anchorId, out var oldNode) || oldNode == null)
			{
				Debug($"[parentRebind] not found anchor by id [{p.anchorId}] (cache lost). Please run land/listAnchors again.");
				return Task.FromResult(MakeParentRebindOptionsResult(null, Array.Empty<TreeNode>(), "Точка не найдена в текущем снимке. Обновите панель."));
			}

			var effectiveNode = ResolveEffectiveAnchorForParentRebind(oldNode);
			var options = BuildParentRebindOptions(effectiveNode, out var message);
			return Task.FromResult(MakeParentRebindOptionsResult(effectiveNode, options, message));
		}

		[JsonRpcMethod("land/rebindAnchorToParent", UseSingleObjectParameterDeserialization = true)]
		public Task<UpdateAnchorResultV2> RebindAnchorToParentAsync(ParentRebindApplyParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.anchorId))
				throw new ArgumentException("anchorId is required");
			if (p.target == null)
				throw new ArgumentException("target is required");

			if (!nodesById.TryGetValue(p.anchorId, out var oldNode) || oldNode == null)
			{
				Debug($"[parentRebind] not found anchor by id [{p.anchorId}] (cache lost). Please run land/listAnchors again.");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			var effectiveNode = ResolveEffectiveAnchorForParentRebind(oldNode);
			var options = BuildParentRebindOptions(effectiveNode, out var _);
			var targetNode = options.FirstOrDefault(x => IsSameTarget(x, p.target));
			if (targetNode == null)
			{
				Debug($"[parentRebind] selected target is stale for anchor [{p.anchorId}].");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			var updated = CloneAnchor(targetNode);
			updated.Id = p.anchorId;
			updated.IsManual = oldNode.IsManual;
			updated.AnchorFamily = EnsureAnchorFamily(updated);

			var isGql = string.Equals(updated.Language, LangGql, StringComparison.OrdinalIgnoreCase);
			var parentGroupId = isGql ? GetTypeGroupId(updated) : null;
			var parentGroupName = isGql ? GetTypeGroupName(updated) : null;
			var fieldGroupId = isGql ? GetFieldGroupId(updated) : null;
			var fieldGroupName = isGql ? GetFieldGroupName(updated) : null;

			nodesById[p.anchorId] = updated;

			try
			{
				lock (markupLock)
				{
					ReplaceAnchorInMarkupUnsafe(updated);
					RebuildRelationsUnsafe();
					RebuildMarkupRootsUnsafe();
					ReloadNodesByIdUnsafe();
					if (!AnchorStore.TrySave(currentFolderPath, currentMarkup, out var err))
						Debug($"[parentRebind] cannot save anchors cache: {err}");
				}
			}
			catch (Exception ex)
			{
				Debug($"[parentRebind] cache persist failed: {ex.Message}");
			}

			return Task.FromResult(MakeUpdateAnchorResult(updated, parentGroupId, parentGroupName, fieldGroupId, fieldGroupName));
		}
	}
}
