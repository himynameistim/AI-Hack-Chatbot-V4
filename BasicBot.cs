// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BasicBot.Dialogs.State;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.BotBuilderSamples
{
    /// <summary>
    /// Main entry point and orchestration for bot.
    /// </summary>
    public class BasicBot : IBot
    {
        private const double PreviousIntentThreshold = 0.6;
        private string previousIntent = null;

        // Supported LUIS Intents
        public const string GreetingIntent = "Greeting";
        public const string CancelIntent = "Cancel";
        public const string HelpIntent = "Help";

        public const string WimbledonVenue = "wimbledon";
        public const string TwickenhamVenue = "twickenham";

        public const string NoneIntent = "None";
        public const string EventFAQIntent = "EventFAQ";

        /// <summary>
        /// Key in the bot config (.bot file) for the LUIS instance.
        /// In the .bot file, multiple instances of LUIS can be configured.
        /// </summary>
        public static readonly string LuisConfiguration = "BasicBotLuisApplication";
        public static readonly string GenericQnAMakerKey = "GenericQnABot";
        public static readonly string WimbledonQnAMakerKey = "WimbledonQnABot";
        public static readonly string TwickenhamQnAMakerKey = "TwickenhamQnABot";
        public static readonly string EventFAQ = "EventFAQ";

        private readonly IStatePropertyAccessor<GreetingState> _greetingStateAccessor;
        private readonly IStatePropertyAccessor<DialogState> _dialogStateAccessor;
        private readonly IStatePropertyAccessor<IntentState> _intentStateAccessor;
        private readonly UserState _userState;
        private readonly ConversationState _conversationState;
        private readonly BotServices _services;

        /// <summary>
        /// Initializes a new instance of the <see cref="BasicBot"/> class.
        /// </summary>
        /// <param name="botServices">Bot services.</param>
        /// <param name="accessors">Bot State Accessors.</param>
        public BasicBot(BotServices services, UserState userState, ConversationState conversationState, ILoggerFactory loggerFactory)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _userState = userState ?? throw new ArgumentNullException(nameof(userState));
            _conversationState = conversationState ?? throw new ArgumentNullException(nameof(conversationState));

            _greetingStateAccessor = _userState.CreateProperty<GreetingState>(nameof(GreetingState));
            _dialogStateAccessor = _conversationState.CreateProperty<DialogState>(nameof(DialogState));
            _intentStateAccessor = _userState.CreateProperty<IntentState>(nameof(IntentState));

            // Verify LUIS configuration.
            if (!_services.LuisServices.ContainsKey(LuisConfiguration))
            {
                throw new InvalidOperationException($"The bot configuration does not contain a service type of `luis` with the id `{LuisConfiguration}`.");
            }

            if (!_services.QnAServices.ContainsKey(GenericQnAMakerKey))
            {
                throw new System.ArgumentException($"Invalid configuration. Please check your '.bot' file for a QnA service named '{GenericQnAMakerKey}'.");
            }

            if (!_services.QnAServices.ContainsKey(WimbledonQnAMakerKey))
            {
                throw new System.ArgumentException($"Invalid configuration. Please check your '.bot' file for a QnA service named '{WimbledonQnAMakerKey}'.");
            }

            if (!_services.QnAServices.ContainsKey(TwickenhamQnAMakerKey))
            {
                throw new System.ArgumentException($"Invalid configuration. Please check your '.bot' file for a QnA service named '{TwickenhamQnAMakerKey}'.");
            }


            Dialogs = new DialogSet(_dialogStateAccessor);
            Dialogs.Add(new GreetingDialog(_greetingStateAccessor, loggerFactory));
        }

        private DialogSet Dialogs { get; set; }

        /// <summary>
        /// Run every turn of the conversation. Handles orchestration of messages.
        /// </summary>
        /// <param name="turnContext">Bot Turn Context.</param>
        /// <param name="cancellationToken">Task CancellationToken.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            var activity = turnContext.Activity;

            // Create a dialog context
            var dc = await Dialogs.CreateContextAsync(turnContext);

            if (activity.Type == ActivityTypes.Message)
            {
                // Perform a call to LUIS to retrieve results for the current activity message.
                var luisResults = await _services.LuisServices[LuisConfiguration].RecognizeAsync(dc.Context, cancellationToken);

                // If any entities were updated, treat as interruption.
                // For example, "no my name is tony" will manifest as an update of the name to be "tony".
                var topScoringIntent = luisResults?.GetTopScoringIntent();

                var topIntent = topScoringIntent.Value.intent;

                //// update greeting state with any entities captured
                //await UpdateGreetingState(luisResults, dc.Context);

                // Handle conversation interrupts first.
                var interrupted = await IsTurnInterruptedAsync(dc, topIntent);
                if (interrupted)
                {
                    // Bypass the dialog.
                    // Save state before the next turn.
                    await _conversationState.SaveChangesAsync(turnContext);
                    await _userState.SaveChangesAsync(turnContext);
                    return;
                }

                // Continue the current dialog
                var dialogResult = await dc.ContinueDialogAsync();

                // if no one has responded,
                if (!dc.Context.Responded)
                {
                    // examine results from active dialog
                    switch (dialogResult.Status)
                    {
                        case DialogTurnStatus.Empty:
                            switch (topIntent)
                            {
                                //case GreetingIntent:
                                //    await dc.BeginDialogAsync(nameof(GreetingDialog));
                                //    break;

                                case NoneIntent:
                                    //await dc.BeginDialogAsync(nameof(GreetingDialog));
                                    await DispatchToQnAMakerAsync(turnContext, GenericQnAMakerKey);
                                    break;

                                case EventFAQIntent:
                                    await DispatchToFAQQnAMakerAsync(luisResults, turnContext);
                                    break;
                                    
                                default:
                                    // Help or no intent identified, either way, let's provide some help.
                                    // to the user
                                    await dc.Context.SendActivityAsync("I didn't understand what you just said to me.");
                                    break;
                            }

                            break;

                        case DialogTurnStatus.Waiting:
                            // The active dialog is waiting for a response from the user, so do nothing.
                            break;

                        case DialogTurnStatus.Complete:
                            await dc.EndDialogAsync();
                            break;

                        default:
                            await dc.CancelAllDialogsAsync();
                            break;
                    }
                }
            }
            //else if (activity.Type == ActivityTypes.ConversationUpdate)
            //{
            //    if (activity.MembersAdded != null)
            //    {
            //        // Iterate over all new members added to the conversation.
            //        foreach (var member in activity.MembersAdded)
            //        {
            //            // Greet anyone that was not the target (recipient) of this message.
            //            // To learn more about Adaptive Cards, see https://aka.ms/msbot-adaptivecards for more details.
            //            if (member.Id != activity.Recipient.Id)
            //            {
            //                var welcomeCard = CreateAdaptiveCardAttachment();
            //                var response = CreateResponse(activity, welcomeCard);
            //                await dc.Context.SendActivityAsync(response);
            //            }
            //        }
            //    }
            //}

            await _conversationState.SaveChangesAsync(turnContext);
            await _userState.SaveChangesAsync(turnContext);
        }

        private async Task DispatchToFAQQnAMakerAsync(RecognizerResult luisResult, ITurnContext context)
        {
            if (!string.IsNullOrEmpty(context.Activity.Text))
            {
                var intentState = await _intentStateAccessor.GetAsync(context, () => new IntentState());

                string eventPlaceName = "Event_PlaceName";
                foreach (var entity in luisResult.Entities)
                {
                    if (entity.Value[eventPlaceName] != null)
                    {
                        intentState.EventPlaceName = entity.Value[eventPlaceName][0]["text"].ToString(); ;
                        await _intentStateAccessor.SetAsync(context, intentState);
                        break;
                    }
                }

                if (intentState.EventPlaceName != null)
                {
                    switch (intentState.EventPlaceName)
                    {
                        case WimbledonVenue:
                            await DispatchToQnAMakerAsync(context, WimbledonQnAMakerKey);
                            break;

                        case TwickenhamVenue:
                            await DispatchToQnAMakerAsync(context, TwickenhamQnAMakerKey);
                            break;
                    }
                }
                else
                {
                    var genericQnAAnswer = await _services.QnAServices[GenericQnAMakerKey].GetAnswersAsync(context);

                    if (genericQnAAnswer.Any() && genericQnAAnswer.First().Score > PreviousIntentThreshold)
                    {
                        await context.SendActivityAsync(genericQnAAnswer.First().Answer);
                    }
                    else
                    {
                        await context.SendActivityAsync($"Can you repeat the question with the venue you are interested in.");
                    }
                }
            }
        }

        // Determine if an interruption has occurred before we dispatch to any active dialog.
        private async Task<bool> IsTurnInterruptedAsync(DialogContext dc, string topIntent)
        {
            // See if there are any conversation interrupts we need to handle.
            if (topIntent.Equals(CancelIntent))
            {
                if (dc.ActiveDialog != null)
                {
                    await dc.CancelAllDialogsAsync();
                    await dc.Context.SendActivityAsync("Ok. I've canceled our last activity.");
                }
                else
                {
                    await dc.Context.SendActivityAsync("I don't have anything to cancel.");
                }

                return true;        // Handled the interrupt.
            }

            if (topIntent.Equals(HelpIntent))
            {
                await dc.Context.SendActivityAsync("Let me try to provide some help.");
                await dc.Context.SendActivityAsync("I understand greetings, being asked for help, or being asked to cancel what I am doing.");
                if (dc.ActiveDialog != null)
                {
                    await dc.RepromptDialogAsync();
                }

                return true;        // Handled the interrupt.
            }

            return false;           // Did not handle the interrupt.
        }

        //// Create an attachment message response.
        //private Activity CreateResponse(Activity activity, Attachment attachment)
        //{
        //    var response = activity.CreateReply();
        //    response.Attachments = new List<Attachment>() { attachment };
        //    return response;
        //}

        //// Load attachment from file.
        //private Attachment CreateAdaptiveCardAttachment()
        //{
        //    var adaptiveCard = File.ReadAllText(@".\Dialogs\Welcome\Resources\welcomeCard.json");
        //    return new Attachment()
        //    {
        //        ContentType = "application/vnd.microsoft.card.adaptive",
        //        Content = JsonConvert.DeserializeObject(adaptiveCard),
        //    };
        //}

        /// <summary>
        /// Helper function to update greeting state with entities returned by LUIS.
        /// </summary>
        /// <param name="luisResult">LUIS recognizer <see cref="RecognizerResult"/>.</param>
        /// <param name="turnContext">A <see cref="ITurnContext"/> containing all the data needed
        /// for processing this conversation turn.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        //private async Task UpdateGreetingState(RecognizerResult luisResult, ITurnContext turnContext)
        //{
        //    if (luisResult.Entities != null && luisResult.Entities.HasValues)
        //    {
        //        // Get latest GreetingState
        //        var greetingState = await _greetingStateAccessor.GetAsync(turnContext, () => new GreetingState());
        //        var entities = luisResult.Entities;

        //        // Supported LUIS Entities
        //        string[] userNameEntities = { "userName", "userName_patternAny" };
        //        string[] userLocationEntities = { "userLocation", "userLocation_patternAny" };

        //        // Update any entities
        //        // Note: Consider a confirm dialog, instead of just updating.
        //        foreach (var name in userNameEntities)
        //        {
        //            // Check if we found valid slot values in entities returned from LUIS.
        //            if (entities[name] != null)
        //            {
        //                // Capitalize and set new user name.
        //                var newName = (string)entities[name][0];
        //                greetingState.Name = char.ToUpper(newName[0]) + newName.Substring(1);
        //                break;
        //            }
        //        }

        //        foreach (var city in userLocationEntities)
        //        {
        //            if (entities[city] != null)
        //            {
        //                // Capitalize and set new city.
        //                var newCity = (string)entities[city][0];
        //                greetingState.City = char.ToUpper(newCity[0]) + newCity.Substring(1);
        //                break;
        //            }
        //        }

        //        // Set the new values into state.
        //        await _greetingStateAccessor.SetAsync(turnContext, greetingState);
        //    }
        //}



        /// <summary>
        /// Dispatches the turn to the request QnAMaker app.
        /// </summary>
        private async Task DispatchToQnAMakerAsync(ITurnContext context, string appName, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!string.IsNullOrEmpty(context.Activity.Text))
            {
                var currentIntentAnswer = await _services.QnAServices[appName].GetAnswersAsync(context);

                    if (currentIntentAnswer.Any())
                    {
                        await context.SendActivityAsync(currentIntentAnswer.First().Answer, cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await context.SendActivityAsync($"Couldn't find an answer in the {appName}.");
                    }
            }
        }
    }
}
