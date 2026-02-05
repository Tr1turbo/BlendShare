using System;
using nadena.dev.ndmf;

namespace Triturbo.BlendShapeShare.Ndmf.Editor
{
    public class Logger
    {
        internal static void Log(ErrorSeverity severity, string key, params object[] objects)
        {
            ErrorReport.ReportError(Localization.L, severity, key, objects);
        }

        internal static void LogFatal(string key, params object[] objects)
        {
            ErrorReport.ReportError(Localization.L, ErrorSeverity.Error, key, objects);
        }

        internal static void LogException(Exception e, string additionalStackTrace = "")
        {
            ErrorReport.ReportException(e, additionalStackTrace);
        }
    }
}