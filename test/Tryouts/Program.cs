using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Basic;
using Newtonsoft.Json.Linq;
using Raven.Server.Documents;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Parser;
using Raven.Server.Utils;
using Sparrow;
using Xunit.Sdk;

namespace Tryouts
{
   
    public static class Program
    {
        private static Process CreateServerProcess()
        {
            var jsonSettings = new JObject
            {
                ["RunInMemory"] = false,
                ["Testing.ParentProcessId"] = Process.GetCurrentProcess().Id,
                ["Setup.Mode"] = "None",
                ["License.Eula.Accepted"] = true,
                ["Security.UnsecuredAccessAllowed"] = "PublicNetwork"
            };
            var path = @"C:\Work\ravendb4\src\Raven.Server\bin\Release\netcoreapp2.2\win-x64\publish\";
            File.WriteAllText(Path.Combine(path , "settings.json"), jsonSettings.ToString());
            var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(path , "Raven.Server.exe"),
                    Arguments = $"-c=\"{Path.Combine(path , "settings.json")}\"",
                    CreateNoWindow = true,
                    ErrorDialog = false,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                }
            };
            return process;
        }

        public static void Main(string[] args)
        {
            var first = new X509Certificate2(@"C:\work\cluster-test\onenode.Cluster.Settings\A\cluster.server.certificate.onenode.pfx");
            var second = new X509Certificate2(@"C:\work\ravendb-v4.1\src\Raven.Server\bin\Debug\netcoreapp2.1\cluster.server.certificate.onenode.pfx");

            var hash1 = CertificateUtils.GetPublicKeyPinningHash(first);
            var hash2 = CertificateUtils.GetPublicKeyPinningHash(second);

            var equals = hash2.Equals(hash1);

            var mre = new ManualResetEventSlim();
           
            var t2 = Task.Run(() =>
            {
                using (var ravenProcess = CreateServerProcess())
                {                    
                    ravenProcess.OutputDataReceived += RavenProcess_OutputDataReceived;
                    ravenProcess.ErrorDataReceived += RavenProcess_ErrorDataReceived;
                    ravenProcess.EnableRaisingEvents = true;
                    ravenProcess.Start();

                    ravenProcess.BeginOutputReadLine();
                    ravenProcess.BeginErrorReadLine();

                    ravenProcess.StandardInput.WriteLine("DELIMITER:ContinuePrinting");
                    ravenProcess.StandardInput.WriteLine("DELIMITER:ReadLine");
                    //ravenProcess.StandardInput.WriteLine("ReadLine");
                    mre.Wait();
                    ravenProcess.Kill();
                }

                Console.WriteLine("WTF?");
            });
            GC.SuppressFinalize(t2);
            AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) => mre.Set();
            

            Console.WriteLine("Hello!!");            
            Thread.Sleep(15000);
            Console.WriteLine("Bye!!");
            mre.Set();
            Task.WaitAll(t2);
        }

        private static void RavenProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            Console.Out.WriteLine(e.Data);
        }

        private static void RavenProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            Console.Out.WriteLine(e.Data);
        }
    }
}
