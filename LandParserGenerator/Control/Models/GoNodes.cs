using Land.Core.Parsing.Tree;
using Land.Markup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Land.Control.Models
{
	public class GoFuncNode
	{
		public ParsedFile ParsedFile { get; set; }
		public Node Node { get; set; }
		public string Reciever { get; set; }
		public string Package { get; set; }
		public string Name { get; set; }
		public float Score { get; set; }
		public float NScore => Score / 4.0f;
		public GoFuncNode(ParsedFile file, Node node, string reciever, string package, string name)
		{
			ParsedFile = file;
			Node = node;
			Reciever = reciever;
			Package = package;
			Name = name;
		}
		public override int GetHashCode()
		{
			return (Node?.ToString() ?? "").GetHashCode() ^ Name.GetHashCode();
		}
		public override bool Equals(object obj)
		{
			return Equals(obj as GoFuncNode);
		}

		public override string ToString()
		{
			return $"{Package}.{Reciever}.{Name}";
		}

		public bool Equals(GoFuncNode obj)
		{
			return obj != null &&
				obj.Name == Name &&
				obj.Node?.ToString() == this.Node?.ToString() &&
				obj.Node?.Children?.Count == this.Node?.Children?.Count;
		}
	}
	public class GoTypeNode
	{
		public Node Node { get; set; }
		public GoType GoType { get; set; }
		public GoTypeNode(Node node, GoType goType)
		{
			Node = node;
			GoType = goType;
		}
	}
	public class GoNodes
	{
		public LinkedList<GoFuncNode> Funcs { get; set; }
		public LinkedList<GoTypeNode> Types { get; set; }
	}
}
