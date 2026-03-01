using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VPTree;
using Land.Core.Parsing.Tree;

namespace MarkupServer
{
	public partial class LandService
	{
		private void BuildCurrentGqlTreeFromDisk()
		{
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			currentGqlAnchors.Clear();
			currentGqlNodes.Clear();

			Tracing.Init();

			var gqlFiles = GetAllFiles(currentFolderPath, "graphql");
			using (Tracing.Tracer.BuildSpan("RebuildGqlIndex").StartActive())
			{
				foreach (var gqlFile in gqlFiles)
				{
					var txt = File.ReadAllText(gqlFile);
					Node root;
					using (Tracing.Tracer.BuildSpan("ParseGql").StartActive())
						root = graphqlParser.Parse(txt).Item1;

					var funcs = GetFuncs(root);
					foreach (var funcsPerType in funcs)
					{
						foreach (var func in funcsPerType.Value)
						{
							var treeNode = GetTreeNodeFromGqlNode(func, gqlFile);
							currentGqlNodes.Add(treeNode);

							currentGqlAnchors.Add(new MethodAnchor
							{
								Id = treeNode.Id,
								ParentNameNorm = treeNode.ParentNameNorm,
								MethodNameNorm = treeNode.MethodNameNorm,
								ReturnTypeNorm = treeNode.ReturnTypeNorm,
								StartOffset = treeNode.StartOffset ?? 0,
								EndOffset = treeNode.EndOffset ?? 0,
								Args = (treeNode.Args ?? new List<Arg>())
									.Select(x => new MethodAnchor.Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm })
									.ToList(),
							});
						}
					}
				}
			}

			using (Tracing.Tracer.BuildSpan("RebuildGqlVPTree").StartActive())
				currentTree = new VPTree<MethodAnchor>(
					currentGqlAnchors,
					(a, b) => Dist.AnchorDistance(a, b, currentWeights),
					42,
					Tracing.Tracer
				);
		}

		[JsonRpcMethod("land/updateAnchor", UseSingleObjectParameterDeserialization = true)]
		public Task<UpdateAnchorResultV2> UpdateAnchorAsync(UpdateAnchorParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.anchorId))
				throw new ArgumentException("anchorId is required");

			// anchorId — это ID "сущности якоря", которое пришло из последнего listAnchors.
			// Оно НЕ обязано соответствовать каким-то id в новом коде. Поэтому:
			// 1) по anchorId берём признаки СТАРОГО якоря из кэша nodesById
			// 2) строим VP-дерево по НОВОМУ коду
			// 3) ищем ближайший узел и возвращаем updatedNode с тем же anchorId

			if (!nodesById.TryGetValue(p.anchorId, out var oldNode) || oldNode == null)
			{
				Debug($"not found anchor by id [{p.anchorId}] (cache lost). Please run land/listAnchors again.");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}
			// Защита: updateAnchor предназначен для перепривязки GraphQL-якорей.
			// Проверяем по префиксу ID, т.к. Name теперь используется как отображаемое имя (MethodName).
			if (!(oldNode.Id ?? "").StartsWith("gql:", StringComparison.OrdinalIgnoreCase))
			{
				Debug($"[updateAnchor] anchor [{p.anchorId}] has Id='{oldNode.Id}', expected prefix 'gql:'. Skipping.");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

try
			{
				BuildCurrentGqlTreeFromDisk();
			}
			catch (Exception ex)
			{
				Debug($"[updateAnchor] cannot rebuild gql tree: {ex.Message}");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			if (currentTree == null || currentGqlAnchors.Count == 0)
				return Task.FromResult(MakeUpdateAnchorResult(null));

			var query = new MethodAnchor
			{
				Id = oldNode.Id,
				ParentNameNorm = oldNode.ParentNameNorm,
				ReturnTypeNorm = oldNode.ReturnTypeNorm,
				MethodNameNorm = oldNode.MethodNameNorm,
				Args = (oldNode.Args ?? new List<Arg>())
					.Select(x => new MethodAnchor.Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm })
					.ToList(),
			};

			var cands = currentTree.KNearest(query, 1);
			if (cands == null || cands.Count == 0)
				return Task.FromResult(MakeUpdateAnchorResult(null));

			var best = cands[0];
			var newNode = currentGqlNodes[best.Index];

			Debug($"[updateAnchor] dist={best.Dist} -> {newNode?.Filepath}:{newNode?.StartOffset}-{newNode?.EndOffset} {newNode?.ParentNameNorm}.{newNode?.MethodNameNorm}");

			// Важно: сохраняем anchorId (сущность), но обновляем позицию/признаки на новые.
			var updated = new TreeNode
			{
				Id = p.anchorId,
				Name = newNode.Name,           // отображаемое имя якоря = MethodName из нового кода
				NodeType = "anchor",
				Filepath = newNode.Filepath,
				StartOffset = newNode.StartOffset,
				EndOffset = newNode.EndOffset,
				ParentNameNorm = newNode.ParentNameNorm,
				ParentNameRaw = newNode.ParentNameRaw,
				MethodNameNorm = newNode.MethodNameNorm,
				ReturnTypeNorm = newNode.ReturnTypeNorm,
				Args = newNode.Args,
			};

			// Новая группа (GraphQL): определяется по {file + type}.
			// groupId должен совпадать с тем, что строится в listAnchors (MakeGroupId("gqlType", file, typeName)).
			var parentType = updated.ParentNameRaw ?? "";
			var parentGroupId = MakeGroupId("gqlType", updated.Filepath, parentType);
			var parentGroupName = string.IsNullOrWhiteSpace(parentType) ? (updated.ParentNameNorm ?? "") : parentType;

			// Обновляем кэш: теперь дальнейшие updateAnchor будут отталкиваться от новой версии.
			nodesById[p.anchorId] = updated;

			// И обновляем персистентный snapshot на диске (если он загружен/создан).
			try
			{
				lock (markupLock)
				{
					if (currentMarkup?.Roots != null)
					{
						AnchorStore.UpsertAnchorInTree(currentMarkup.Roots, updated, parentGroupId, parentGroupName);
						if (!AnchorStore.TrySave(currentFolderPath, currentMarkup, out var err))
							Debug($"[updateAnchor] cannot save anchors cache: {err}");
					}
				}
			}
			catch (Exception ex)
			{
				Debug($"[updateAnchor] cache persist failed: {ex.Message}");
			}

			return Task.FromResult(MakeUpdateAnchorResult(updated, parentGroupId, parentGroupName));
		}
	}
}
