using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using F23.StringSimilarity;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs.Extensions.SignalRService;

namespace ServerlessTrivia
{
    public static class Functions
    {
        private const int secondsBetweenClues = 20;

        [FunctionName(nameof(TriviaOrchestrator))]
        public static async Task TriviaOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContext context, ILogger logger)
        {
            Clue previousClue = null;
            try
            {
                previousClue = context.GetInput<Clue>();
                var outputs = new List<string>();

                var nextRun = context.CurrentUtcDateTime.AddSeconds(secondsBetweenClues);

                var clue = await context.CallActivityAsync<Clue>(nameof(GetAndSendClue), (previousClue, nextRun));
                logger.LogInformation(JsonConvert.SerializeObject(clue));

                logger.LogInformation($"*** Next run: {nextRun.ToString()}");
                await context.CreateTimer(nextRun, CancellationToken.None);

                previousClue = clue;
            }
            catch (Exception ex)
            {
                logger.LogError(ex.ToString());
            }
            finally
            {
                context.ContinueAsNew(previousClue);
            }
        }

        [FunctionName(nameof(GetAndSendClue))]
        public static async Task<Clue> GetAndSendClue(
            [ActivityTrigger] DurableActivityContext context,
            [Table("clues")] IAsyncCollector<Clue> clues,
            [SignalR(HubName = "trivia")] IAsyncCollector<SignalRMessage> signalRMessages,
            ILogger logger)
        {
            logger.LogInformation($"*** Getting clue...");
            var client = System.Net.Http.HttpClientFactory.Create();
            var responseJson = await client.GetStringAsync("http://jservice.io/api/random");
            var response = JsonConvert.DeserializeObject<IEnumerable<JServiceResponse>>(responseJson);

            var (previousClue, nextRun) = context.GetInput<(Clue, DateTime)>();

            var clue = response.First().ToClue();
            await clues.AddAsync(clue);

            var now = DateTime.UtcNow;
            var timeRemaining = nextRun > now ? nextRun.Subtract(now) : TimeSpan.FromSeconds(0);

            await signalRMessages.AddAsync(new SignalRMessage
            {
                Target = "newClue",
                Arguments = new object[]
                {
                    new {
                        previousClue = previousClue,
                        nextClue = new {
                            clueId = clue.PartitionKey,
                            question = clue.Question,
                            categoryTitle = clue.CategoryTitle,
                            estimatedTimeRemaining = timeRemaining.TotalMilliseconds
                        }
                    }
                }
            });
            return clue;
        }

        [FunctionName(nameof(SubmitGuess))]
        public static async Task<object> SubmitGuess(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")]Guess guess,
            [Table("clues", "{clueId}", Clue.RowKeyValue)] Clue clue,
            [Table("guesses")] IAsyncCollector<Guess> guesses,
            [SignalR(HubName = "trivia")] IAsyncCollector<SignalRMessage> signalRMessages)
        {
            var l = new NormalizedLevenshtein();
            var similarity = l.Similarity(NormalizeString(clue.Answer), NormalizeString(guess.Value));

            await guesses.AddAsync(guess);

            var result = new
            {
                guess.SessionId,
                ClueId = clue.PartitionKey,
                Guess = guess.Value,
                IsCorrect = similarity > 0.75,
                Similarity = similarity
            };

            await signalRMessages.AddAsync(new SignalRMessage
            {
                Target = "newGuess",
                Arguments = new object[]
                {
                    new {
                        clueId = result.ClueId,
                        isCorrect = result.IsCorrect
                    }
                }
            });

            return result;
        }

        private static string NormalizeString(string input)
        {
            return Regex.Replace(input, @"(\b(a|an|the|of)\b|[^a-zA-Z0-9]+)", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        }

        [FunctionName("HttpStartSingle")]
        public static async Task<HttpResponseMessage> RunSingle(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestMessage req,
            [OrchestrationClient] DurableOrchestrationClient starter,
            TraceWriter log)
        {
            const string instanceId = "1";
            // Check if an instance with the specified ID already exists.
            var existingInstance = await starter.GetStatusAsync(instanceId);
            if (existingInstance == null)
            {
                await starter.StartNewAsync(nameof(TriviaOrchestrator), instanceId);
                log.Info($"Started orchestration with ID = '{instanceId}'.");
                return starter.CreateCheckStatusResponse(req, instanceId);
            }
            else
            {
                // An instance with the specified ID exists, don't create one.
                return req.CreateErrorResponse(
                    HttpStatusCode.Conflict,
                    $"An instance with ID '{instanceId}' already exists.");
            }
        }

        [FunctionName(nameof(SignalRInfo))]
        public static IActionResult SignalRInfo(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")]HttpRequestMessage req, 
            [SignalRConnectionInfo(HubName = "trivia")]AzureSignalRConnectionInfo info, 
            ILogger logger)
        {
            return info != null
                ? (ActionResult)new OkObjectResult(info)
                : new NotFoundObjectResult("Failed to load SignalR Info.");
        }
    }
}