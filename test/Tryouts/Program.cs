using System;
using System.IO;
using System.Threading.Tasks;
using FastTests.Server.Documents.Queries.Parser;
using Microsoft.Extensions.Logging;
using SlowTests.Client;
using SlowTests.Issues;
using SlowTests.MailingList;
using Sparrow.Logging;

namespace Tryouts
{
    public static class Program
    {

        public static async Task Main(string[] args)
        {
            LoggingSource.Instance.SetupLogMode(LogMode.Information, @"c:\work\logs\tryouts");

            try
            {
                using (var test = new SlowTests.Authentication.AuthenticationLetsEncryptTests())
                {
                    await test.CanGetLetsEncryptCertificateAndRenewIt();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }


        }
    }
}
