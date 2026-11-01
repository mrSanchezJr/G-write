using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace G_write
{
    public partial class NumpadWindow : Window
    {
        public event EventHandler<string>? NumberPressed;
        public event EventHandler? BackspacePressed;
        public event EventHandler? EnterPressed;
        public event EventHandler? ClearPressed;

        public bool AllowClose { get; set; } = false;

        public NumpadWindow()
        {
            InitializeComponent();

            Closing += (sender, e) =>
            {
                if (!AllowClose)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
        }

        public void SetTargetName(string name)
        {
            if (lblTargetField != null)
            {
                lblTargetField.Text = $"Поле: {name}";
            }
        }

        private void Button_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tag)
            {
                switch (tag)
                {
                    case "←":
                        BackspacePressed?.Invoke(this, EventArgs.Empty);
                        break;
                    case "Ввод":
                        EnterPressed?.Invoke(this, EventArgs.Empty);
                        break;
                    case "Стереть":
                        ClearPressed?.Invoke(this, EventArgs.Empty);
                        break;
                    default:
                        NumberPressed?.Invoke(this, tag);
                        break;
                }
            }
        }
    }
}
