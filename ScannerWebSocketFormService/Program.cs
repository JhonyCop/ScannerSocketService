using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScannerWebSocketFormService
{
    static class Program
    {
        /// <summary>
        /// Punto de entrada principal de la aplicación.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Solo configurar si no está ya configurado
            ConfigurarInicioSiEsNecesario();

            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }

        static void ConfigurarInicioSiEsNecesario()
        {
            try
            {
                using RegistryKey clave = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

                // Solo agregar si no existe
                if (clave?.GetValue("ScannerWebSocketFormService") == null)
                {
                    clave.SetValue("ScannerWebSocketFormService", $"\"{Application.ExecutablePath}\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al intentar registrar la app en el inicio de Windows:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}