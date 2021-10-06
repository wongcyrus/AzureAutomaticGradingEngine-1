using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using AzureAutomaticGradingEngineFunctionApp.Helper;
using AzureAutomaticGradingEngineFunctionApp.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.WindowsAzure.Storage.Queue;

namespace AzureAutomaticGradingEngineFunctionApp
{
    public static class ScheduleGraderFunction
    {
        [FunctionName("ScheduleGrader")]
        public static async Task ScheduleGrader(
            [TimerTrigger("0 0 */12 * * *")] TimerInfo myTimer,
            //[TimerTrigger("0 0 * * * *")] TimerInfo myTimer,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            if (myTimer.IsPastDue)
            {
                log.LogInformation("Timer is running late!");
            }
            string instanceId = await starter.StartNewAsync("ScheduleGraderOrchestrationFunction", null);
            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }

        [FunctionName("ManualRunScheduleGraderOrchestrationFunction")]
        public static async Task ManualGrader(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req, ILogger log, ExecutionContext context,
            [DurableClient] IDurableOrchestrationClient starter
        )
        {
            var instanceId = await starter.StartNewAsync("ScheduleGraderOrchestrationFunction", null);
            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }

        [FunctionName("ScheduleGraderOrchestrationFunction")]
        public static async Task RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var assignments = await context.CallActivityAsync<List<Assignment>>("GetAssignmentList", null);

            Console.WriteLine(assignments.Count());
            var classJobs = new Task<ClassGradingJob>[assignments.Count()];
            for (var i = 0; i < assignments.Count(); i++)
            {
                classJobs[i] = context.CallActivityAsync<ClassGradingJob>(
                    "GradeAssignment",
                    assignments[i]);
            }
            await Task.WhenAll(classJobs);


            foreach (var r in classJobs)
            {
                var classGradingJob = r.Result;
                foreach (dynamic student in classGradingJob.students)
                {
                    await context.CallActivityAsync<SingleGradingJob>(
                        "RunAndSaveTestResult",
                        new SingleGradingJob
                        {
                            assignment = classGradingJob.assignment,
                            graderUrl = classGradingJob.graderUrl,
                            student = student
                        });
                }

                // Parallel mode code is working due to Azure Function cannot run NUnit in parallel.
                //var gradingTasks = new Task<SingleGradingJob>[classGradingJob.students.Count];
                //var i = 0;
                //foreach (dynamic student in classGradingJob.students)
                //{
                //    gradingTasks[i] = context.CallActivityAsync<SingleGradingJob>(
                //        "RunAndSaveTestResult",
                //        new SingleGradingJob
                //        {
                //            assignment = classGradingJob.assignment,
                //            graderUrl = classGradingJob.graderUrl,
                //            student = student
                //        });
                //    i++;
                //}
                //await Task.WhenAll(gradingTasks);
            }

            var task2s = new Task[assignments.Count()];
            for (var i = 0; i < assignments.Count(); i++)
            {
                task2s[i] = context.CallActivityAsync(
                    "SaveMarkJson",
                    assignments[i].Name);
            }
            await Task.WhenAll(task2s);


            Console.WriteLine("Completed!");
        }


        [FunctionName("GetAssignmentList")]
        public static async Task<List<Assignment>> GetAssignmentList([ActivityTrigger] string name, ExecutionContext executionContext,
    ILogger log)
        {
            CloudStorageAccount storageAccount = CloudStorage.GetCloudStorageAccount(executionContext);

            CloudTableClient cloudTableClient = storageAccount.CreateCloudTableClient();
            CloudTable assignmentsTable = cloudTableClient.GetTableReference("assignments");
            CloudTable credentialsTable = cloudTableClient.GetTableReference("credentials");

            TableContinuationToken token = null;
            var assignments = new List<AssignmentTableEntity>();
            do
            {
                var queryResult = await assignmentsTable.ExecuteQuerySegmentedAsync(new TableQuery<AssignmentTableEntity>(), token);
                assignments.AddRange(queryResult.Results);
                token = queryResult.ContinuationToken;
            } while (token != null);

            var results = new List<Assignment>();
            foreach (var assignment in assignments)
            {
                string graderUrl = assignment.GraderUrl;
                string project = assignment.PartitionKey;

                var credentialsTableEntities = new List<CredentialsTableEntity>();
                do
                {
                    var queryResult = await credentialsTable.ExecuteQuerySegmentedAsync(
                        new TableQuery<CredentialsTableEntity>().Where(TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, project)), token);
                    credentialsTableEntities.AddRange(queryResult.Results);
                    token = queryResult.ContinuationToken;
                } while (token != null);


                var students = credentialsTableEntities.Select(c => new
                {
                    email = c.RowKey,
                    credentials = c.Credentials
                }).ToArray();


                results.Add(new Assignment
                {
                    Name = project,
                    TeacherEmail = assignment.TeacherEmail,
                    Context = new ClassContext() { GraderUrl = graderUrl, Students = JsonConvert.SerializeObject(students) }
                });

            }
            return results;
        }
        

        [FunctionName("GradeAssignment")]
        public static async Task<ClassGradingJob> GradeAssignment([ActivityTrigger] Assignment assignment, ExecutionContext context,
ILogger log)
        {
            string graderUrl = assignment.Context.GraderUrl;
            dynamic students = JsonConvert.DeserializeObject(assignment.Context.Students);

            Console.WriteLine(assignment.Name + ":" + students.Count);
            return new ClassGradingJob() { assignment = assignment, graderUrl = graderUrl, students = students };
        }

        [FunctionName("RunAndSaveTestResult")]
        public static async Task RunAndSaveTestResult([ActivityTrigger] SingleGradingJob job, ExecutionContext context)
        {
            CloudStorageAccount storageAccount = CloudStorage.GetCloudStorageAccount(context);
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = blobClient.GetContainerReference("testresult");

            var cloudQueueClient = storageAccount.CreateCloudQueueClient();
            var queue = cloudQueueClient.GetQueueReference("messages");

            var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(3);
            var queryPair = new NameValueCollection();
            queryPair.Set("credentials", job.student.credentials.ToString());
            queryPair.Set("trace", job.student.email.ToString());

            var uri = new Uri(job.graderUrl + ToQueryString(queryPair));
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var xml = await client.GetStringAsync(uri);
                var now = DateTime.Now;
                await SaveTestResult(container, job.assignment.Name, job.student.email.ToString(), xml, now);
                await SendTestResultToStudent(queue, job.assignment.Name, job.student.email.ToString(), xml, now);
                watch.Stop();
                var elapsedMs = watch.ElapsedMilliseconds;
                Console.WriteLine(job.student.email + " get test result in " + elapsedMs + "ms.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(job.student.email + " in error.");
                Console.WriteLine(ex);
            }
        }

        private static string ToQueryString(NameValueCollection nvc)
        {
            var array = (
                from key in nvc.AllKeys
                from value in nvc.GetValues(key)
                select $"{HttpUtility.UrlEncode(key)}={HttpUtility.UrlEncode(value)}"
            ).ToArray();
            return "?" + string.Join("&", array);
        }

        private static async Task SaveTestResult(CloudBlobContainer container, string assignment, string email, string xml, DateTime now)
        {

            var filename = Regex.Replace(email, @"[^0-9a-zA-Z]+", "");
            var blobName = string.Format(CultureInfo.InvariantCulture, assignment + "/" + email + "/{0:yyyy/MM/dd/HH/mm}/" + filename + ".xml", now);
            Console.WriteLine(blobName);

            CloudBlockBlob blob = container.GetBlockBlobReference(blobName);
            blob.Properties.ContentType = "application/xml";
            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms);
            await writer.WriteAsync(xml);
            await writer.FlushAsync();
            ms.Position = 0;
            await blob.UploadFromStreamAsync(ms);
        }

        private static async Task SendTestResultToStudent(CloudQueue queue, string assignment, string email, string xml,
            DateTime now)
        {
            var nUnitTestResult = GradeReportFunction.ParseNUnitTestResult(xml);

            var totalMark = nUnitTestResult.Sum(c => c.Value);

            var marks = String.Join("",
                nUnitTestResult.OrderBy(c => c.Key).Select(c => c.Key + ": " + c.Value + "\n").ToArray());

            var body = $@"
Dear Student,

You have just earned {totalMark} mark(s).

{marks}

Regards,
Azure Automatic Grading Engine

Raw XML:
{xml}
";
            var emailMessage = new EmailMessage
            {
                To = email,
                Subject = $"Your {assignment} Mark at {now}",
                Body = body
            };

            await queue.AddMessageAsync(new CloudQueueMessage(JsonConvert.SerializeObject(emailMessage)));
        }

        [FunctionName("SaveMarkJson")]
        public static async Task SaveMarkJson([ActivityTrigger] string assignment,
            ExecutionContext executionContext,
            ILogger log)
        {
            var now = DateTime.Now;
            var accumulatedMarks = await GradeReportFunction.CalculateMarks(log, executionContext, assignment, false);
            var blobName = string.Format(CultureInfo.InvariantCulture, assignment + "/{0:yyyy/MM/dd/HH/mm}/accumulatedMarks.json", now);
            await SaveJsonReport(executionContext, blobName, accumulatedMarks);
            blobName = assignment + "/accumulatedMarks.json";
            await SaveJsonReport(executionContext, blobName, accumulatedMarks);
            var todayMarks = await GradeReportFunction.CalculateMarks(log, executionContext, assignment, true);
            blobName = string.Format(CultureInfo.InvariantCulture, assignment + "/{0:yyyy/MM/dd/HH/mm}/todayMarks.json", now);
            await SaveJsonReport(executionContext, blobName, todayMarks);
            blobName = assignment + "/todayMarks.json";
            await SaveJsonReport(executionContext, blobName, todayMarks);
        }

        private static async Task SaveJsonReport(ExecutionContext executionContext, string blobName, Dictionary<string, Dictionary<string, int>> calculateMarks)
        {
            CloudStorageAccount storageAccount = CloudStorage.GetCloudStorageAccount(executionContext);
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = blobClient.GetContainerReference("report");
            CloudBlockBlob blob = container.GetBlockBlobReference(blobName);
            blob.Properties.ContentType = "application/json";
            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms);
            await writer.WriteAsync(JsonConvert.SerializeObject(calculateMarks));
            await writer.FlushAsync();
            ms.Position = 0;
            await blob.UploadFromStreamAsync(ms);
        }
    }
}