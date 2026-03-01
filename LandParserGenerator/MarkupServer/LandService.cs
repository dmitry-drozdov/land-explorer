using Land.Core;
using Land.Core.Parsing;
using Land.Core.Parsing.Tree;
using Land.Core.Specification;
using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		// Текущий snapshot разметки (Roots), загруженный/созданный для currentFolderPath.
		// Нужен, чтобы updateAnchor мог обновлять файл на диске.
		private PersistedMarkup currentMarkup;
		private readonly object markupLock = new object();

		private BaseParser graphqlParser;
		private BaseParser typescriptParser;
		// Все узлы, которые мы отдали в дерево (anchor'ы нужны для updateAnchor).
		// ВАЖНО: ключ должен быть глобально уникальным на весь folder (иначе коллизии при нескольких файлах).
		private Dictionary<string, TreeNode> nodesById = [];

		// Кэш последнего построенного дерева для папки (чтобы updateAnchor работал на весь folder,
		// и не было жёстко зашитых путей).
		private string currentFolderPath;
		private List<MethodAnchor> currentGqlAnchors = new();
		private List<TreeNode> currentGqlNodes = new();
		private VPTree<MethodAnchor> currentTree;
		private Dist.Weights currentWeights = new();


		private static TreeNodeClient ToClientNode(TreeNode n)
		{
			if (n == null) return null;
			var isAnchor = string.Equals(n.NodeType, "anchor", StringComparison.OrdinalIgnoreCase);
			var c = new TreeNodeClient
			{
				Id = n.Id,
				Name = n.Name,
				NodeType = n.NodeType,
			};

			// Оптимизация трафика: file/offsets нужны только для anchor-нод.
			if (isAnchor)
			{
				c.Filepath = n.Filepath;
				c.StartOffset = n.StartOffset;
				c.EndOffset = n.EndOffset;
			}

			if (n.Children != null && n.Children.Count > 0)
				c.Children = n.Children.Select(ToClientNode).Where(x => x != null).ToList();
			return c;
		}

		private static List<TreeNodeClient> ToClientRoots(List<TreeNode> roots)
			=> roots?.Select(ToClientNode).Where(x => x != null).ToList() ?? new List<TreeNodeClient>();

		[JsonRpcMethod("shutdown")]
		public Task ShutdownAsync() => Task.CompletedTask;

		[JsonRpcMethod("exit")]
		public void Exit() => Environment.Exit(0);
	}
}
