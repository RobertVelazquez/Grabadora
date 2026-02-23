using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;

namespace Grabadora
{
    public class ProgressForm : Form
    {
        private ProgressBar _progressBar;
        private Label _lblStatus;
        private Button _btnCancel;
        public CancellationTokenSource CancellationTokenSource { get; } = new CancellationTokenSource();

        public ProgressForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Exportando Audio...";
            this.Size = new Size(400, 150);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ControlBox = false; // Evitar cierre con X sin cancelar
            this.BackColor = Color.FromArgb(45, 45, 48);
            this.ForeColor = Color.White;

            _lblStatus = new Label 
            { 
                Text = "Procesando... 0%", 
                Location = new Point(20, 20), 
                AutoSize = true 
            };

            _progressBar = new ProgressBar 
            { 
                Location = new Point(20, 50), 
                Size = new Size(340, 23),
                Style = ProgressBarStyle.Continuous
            };

            _btnCancel = new Button 
            { 
                Text = "Cancelar", 
                Location = new Point(150, 85), 
                Size = new Size(100, 30),
                BackColor = Color.FromArgb(60, 60, 60),
                FlatStyle = FlatStyle.Flat
            };
            _btnCancel.Click += (s, e) => { CancellationTokenSource.Cancel(); _lblStatus.Text = "Cancelando..."; _btnCancel.Enabled = false; };

            this.Controls.AddRange(new Control[] { _lblStatus, _progressBar, _btnCancel });
        }

        public void ReportProgress(double value)
        {
            int percent = (int)(value * 100);
            _progressBar.Value = Math.Min(100, Math.Max(0, percent));
            _lblStatus.Text = $"Procesando... {percent}%";
        }
    }
}