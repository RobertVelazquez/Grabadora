using System;
using System.Windows.Forms;

namespace Grabadora
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                // Configuración para pantallas de alta resolución (High DPI)
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error fatal al iniciar la aplicación:\n{ex}", "Error Fatal", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}