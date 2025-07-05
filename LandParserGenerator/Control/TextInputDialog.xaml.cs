using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Land.Control
{
	/// <summary>
	/// Логика взаимодействия для TextInputDialog.xaml
	/// </summary>
	public partial class TextInputDialog : Window, INotifyPropertyChanged
	{
		public event PropertyChangedEventHandler PropertyChanged;

		public string Title { get; set; }
		public string Question { get; set; }

		private string _defaultText;
		public string DefaultText
		{
			get => _defaultText;
			set
			{
				_defaultText = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DefaultText)));
			}
		}

		public string ResponseText { get; private set; }

		public TextInputDialog()
		{
			InitializeComponent();
			DataContext = this;
			InputBox.Focus();
			InputBox.SelectAll();
		}

		private void OkButton_Click(object sender, RoutedEventArgs e)
		{
			ResponseText = InputBox.Text;
			DialogResult = true;
		}

		private void CancelButton_Click(object sender, RoutedEventArgs e)
		{
			DialogResult = false;
		}
	}
}
