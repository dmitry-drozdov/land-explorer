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
		public Dictionary<Node, List<BorderPoint>> BorderPointsByNode { get; set; } = new Dictionary<Node, List<BorderPoint>>();

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
			}

			base.Visit(node);
		}

		public static Dictionary<string, List<Node>> GetGroups(Node root, IEnumerable<string> targetTypes)
		{
			var visitor = new GroupNodesByTypeVisitor(targetTypes);

			root.Accept(visitor);

			return visitor.Grouped;
		}

		public void RecalcBorderpoints()
		{
			foreach (var g in Grouped)
			{
				var lst = new List<BorderPoint>();
				foreach (var node in g.Value)
				{
					var start = new BorderPoint
					{
						Node = node,
						Offset = node.Location.Start.Offset,
					};
					var end = new BorderPoint
					{
						Node = node,
						Offset = node.Location.End.Offset,
					};

					lst.Add(start);
					lst.Add(end);

					if (BorderPointsByNode.TryGetValue(node, out var points))
					{
						points.Add(start);
						points.Add(end);
					}
					else
					{
						points = new List<BorderPoint> { start, end };
						BorderPointsByNode.Add(node, points);
					}
				}

				BorderPoints[g.Key] = lst.OrderBy(e => e.Offset).ToList();
			}
		}
	}
}
