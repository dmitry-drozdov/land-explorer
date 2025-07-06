using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.ComponentModel;
using Land.Markup.Tree;

namespace Land.Markup
{
	public abstract class MarkupElement: INotifyPropertyChanged
	{
		public Guid Id { get; set; } = Guid.NewGuid();

		private string _name;
		private string _comment;
		private string _documentation;

		public string Name {
			get => _name;
			set
			{
				_name = value;

				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
			}
		}

		public string Comment
		{
			get => _comment;
			set
			{
				_comment = value;

				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Comment)));
			}
		}


		public string Documentation
		{
			get => _documentation;
			set
			{
				_documentation = value;

				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Documentation)));
			}
		}


		[JsonIgnore]
		public Concern Parent { get; set; }

		public event PropertyChangedEventHandler PropertyChanged;

		public abstract void Accept(BaseMarkupVisitor visitor);

		public override string ToString()
		{
			return $"{Id} {Name}";
		}
	}
}
