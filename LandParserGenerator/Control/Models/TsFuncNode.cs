using Land.Core.Parsing.Tree;
using Land.Markup;
using System;
using System.Collections.Generic;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Land.Control.Models
{
	public class TsFuncNode
	{
		public ParsedFile ParsedFile { get; set; }
		public Node Node { get; set; }
		public string Name { get; set; }
		public string ClassName { get; set; }
		public List<string> Args { get; set; }

		public TsFuncNode(ParsedFile parsedFile, Node node, string name, string className, List<string> args) { 
			this.ParsedFile = parsedFile;
			this.Node = node;
			this.Name = name;
			this.ClassName = className;
			this.Args = args;
		}

		public override int GetHashCode()
		{
			return (Node?.ToString() ?? "").GetHashCode() ^ Name.GetHashCode();
		}
		public override bool Equals(object obj)
		{
			return Equals(obj as TsFuncNode);
		}

		public override string ToString()
		{
			return $"{ClassName}.{Name} ({Args.Count})";
		}

		public bool Equals(TsFuncNode obj)
		{
			return obj != null &&
				obj.Name == Name &&
				obj.Node?.ToString() == this.Node?.ToString() &&
				obj.Node?.Children?.Count == this.Node?.Children?.Count;
		}
	}
}
