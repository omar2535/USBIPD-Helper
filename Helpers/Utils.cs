using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace USBIPD_Helper.Helpers
{
    class Utils
    {
        public static bool IsRunningAsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity is not null &&
                   new WindowsPrincipal(identity)
                       .IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
