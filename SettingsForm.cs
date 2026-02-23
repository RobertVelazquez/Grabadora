using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;

namespace Grabadora
{
    public class SettingsForm : Form
    {
        private AudioEngine _engine;
        private ComboBox _cbDriverType;
        private ComboBox _cbInput;
        private ComboBox _cbOutput;

        public SettingsForm(AudioEngine engine)
        {
            _engine = engine;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Configuración de Audio";
            this.Size = new Size(400, 320); // Aumentar altura para asegurar visibilidad de botones
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(45, 45, 48);
            this.ForeColor = Color.White;

            var lblType = new Label { Text = "Tipo de Driver:", Location = new Point(20, 20), AutoSize = true };
            _cbDriverType = new ComboBox { Location = new Point(20, 45), Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
            _cbDriverType.Items.AddRange(new object[] { "Windows Audio (WaveOut)", "ASIO (Baja Latencia)" });
            _cbDriverType.SelectedIndex = _engine.IsAsio ? 1 : 0;
            _cbDriverType.SelectedIndexChanged += (s, e) => UpdateDeviceLists();

            var lblInput = new Label { Text = "Entrada / Driver ASIO:", Location = new Point(20, 80), AutoSize = true };
            _cbInput = new ComboBox { Location = new Point(20, 105), Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };

            var lblOutput = new Label { Text = "Salida (Solo Windows Audio):", Location = new Point(20, 140), AutoSize = true };
            _cbOutput = new ComboBox { Location = new Point(20, 165), Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };

            var btnOk = new Button { Text = "Aceptar", Location = new Point(190, 230), Size = new Size(80, 30), DialogResult = DialogResult.OK, BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat };
            var btnCancel = new Button { Text = "Cancelar", Location = new Point(280, 230), Size = new Size(80, 30), DialogResult = DialogResult.Cancel, BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat };

            btnOk.Click += BtnOk_Click;

            this.Controls.AddRange(new Control[] { lblType, _cbDriverType, lblInput, _cbInput, lblOutput, _cbOutput, btnOk, btnCancel });
            
            UpdateDeviceLists();
        }

        private void UpdateDeviceLists()
        {
            bool isAsio = _cbDriverType.SelectedIndex == 1;
            _cbOutput.Enabled = !isAsio; // ASIO maneja entrada y salida juntas

            if (isAsio)
            {
                LoadAsioDrivers();
            }
            else
            {
                LoadWindowsDevices();
            }
        }

        private void LoadAsioDrivers()
        {
            _cbInput.Items.Clear();
            var drivers = _engine.GetAsioDrivers();
            if (drivers.Count > 0)
            {
                _cbInput.Items.AddRange(drivers.ToArray());
                _cbInput.SelectedIndex = 0; // Seleccionar el primero por defecto
            }
            else
            {
                _cbInput.Items.Add("No se encontraron drivers ASIO");
                _cbInput.Enabled = false;
            }
        }

        private void LoadWindowsDevices()
        {
            _cbInput.Enabled = true;
            // Cargar Entradas
            var inputs = _engine.GetInputDevices();
            _cbInput.Items.Clear();
            if (inputs.Count > 0)
            {
                _cbInput.Items.AddRange(inputs.ToArray());
                // Seleccionar el actual
                if (_engine.InputDeviceNumber >= 0 && _engine.InputDeviceNumber < inputs.Count)
                    _cbInput.SelectedIndex = _engine.InputDeviceNumber;
                else
                    _cbInput.SelectedIndex = 0;
            }
            else
            {
                _cbInput.Items.Add("No se encontraron micrófonos");
                _cbInput.Enabled = false;
            }

            // Cargar Salidas
            var outputs = _engine.GetOutputDevices();
            _cbOutput.Items.Clear();
            if (outputs.Count > 0)
            {
                _cbOutput.Items.AddRange(outputs.ToArray());
                // Seleccionar el actual (si es -1 mapper, seleccionamos 0 o mostramos mapper si lo agregamos)
                // NAudio usa 0, 1, 2... para dispositivos específicos.
                if (_engine.OutputDeviceNumber >= 0 && _engine.OutputDeviceNumber < outputs.Count)
                    _cbOutput.SelectedIndex = _engine.OutputDeviceNumber;
                else
                    _cbOutput.SelectedIndex = 0;
            }
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            try
            {
                if (_cbDriverType.SelectedIndex == 1) // Modo ASIO
                {
                    if (_cbInput.SelectedItem != null && _cbInput.Enabled)
                        _engine.SetAsioDriver(_cbInput.SelectedItem.ToString());
                }
                else // Modo Windows Audio
                {
                    // SetOutputDevice se encarga de la transición desde ASIO si es necesario.
                    if (_cbOutput.SelectedIndex >= 0)
                        _engine.SetOutputDevice(_cbOutput.SelectedIndex);
                    
                    if (_cbInput.Enabled && _cbInput.SelectedIndex >= 0)
                        _engine.InputDeviceNumber = _cbInput.SelectedIndex;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al configurar audio: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            
            this.Close();
        }
    }
}