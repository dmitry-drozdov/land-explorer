using System;
using System.Collections.Generic;
using System.Linq;
using Land.Core.Parsing.Tree;
using Land.Markup.Binding;

namespace Land.Markup.CoreExtension
{
	public class GroupNodesByTypeVisitor : BaseTreeVisitor
	{
		public Dictionary<string, List<Node>> Grouped { get; set; } = new Dictionary<string, List<Node>>();
		public Dictionary<string, List<BorderPoint>> BorderPoints { get; set; } = new Dictionary<string, List<BorderPoint>>();

		public GroupNodesByTypeVisitor(IEnumerable<string> targetTypes)
		{
			Grouped = targetTypes.ToDictionary(e => e, e => new List<Node>());
			RecalcBorderpoints();
		}

		public override void Visit(Node node)
		{
			if (Grouped.ContainsKey(node.Type)
				&& node.Location != null)
			{
				Grouped[node.Type].Add(node);
				RecalcBorderpoints();
			}

			base.Visit(node);
		}

		public static Dictionary<string, List<Node>> GetGroups(Node root, IEnumerable<string> targetTypes)
		{
			var visitor = new GroupNodesByTypeVisitor(targetTypes);

			root.Accept(visitor);

			return visitor.Grouped;
		}

		private void RecalcBorderpoints()
		{
			foreach (var g in Grouped)
			{
				BorderPoints[g.Key] = g.Value.SelectMany(e => new List<BorderPoint>
				{
					new BorderPoint
					{
						Node = e,
						Offset = e.Location.Start.Offset,
					},
					new BorderPoint
					{
						Node = e,
						Offset = e.Location.End.Offset,
					},
				}).OrderBy(e => e.Offset).ToList();
			}
		}
	}
}
