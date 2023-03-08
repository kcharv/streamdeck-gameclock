using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceModel.Channels;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;

namespace gameclock
{
    [PluginActionId("com.clydethedog.gameclock")]
    
    public class gameclock : KeyAndEncoderBase
    {
        private class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings()
            {
                PluginSettings instance = new PluginSettings();
                instance.GameClockFile = String.Empty;
                instance.GameClockTime = "02:00";
                return instance;
            }

            [FilenameProperty]
            [JsonProperty(PropertyName = "gameClockFile")]
            public string GameClockFile { get; set; }

            [JsonProperty(PropertyName = "gameClockTime")]
            public string GameClockTime { get; set; }

        }

        #region Private Members

        private const int RESET_COUNTER_KEYPRESS_LENGTH = 1;

        private Timer tmrGameClock;
        private PluginSettings settings;
        private bool keyPressed = false;
        private bool dialWasRotated = false;
        private DateTime keyPressStart;
        private long gameClockSeconds;
        private readonly int stepSize = 1;
        private bool isGameClockRunning;

        #endregion

        public gameclock(SDConnection connection, InitialPayload payload) : base(connection, payload)
        {
            if (payload.Settings == null || payload.Settings.Count == 0)
            {
                this.settings = PluginSettings.CreateDefaultSettings();
                SaveSettings();
            }
            else
            {
                this.settings = payload.Settings.ToObject<PluginSettings>();
            }
            Logger.Instance.LogMessage(TracingLevel.INFO, "initial run");
            ResetCounter();                                                 //Just started, set to 0
        }

        public override void Dispose()
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, $"Destructor called");
        }

        public override void KeyPressed(KeyPayload payload)
        {
            //long press parameters
            keyPressStart = DateTime.Now;
            keyPressed = true;

            Logger.Instance.LogMessage(TracingLevel.INFO, "Key Pressed");

            if  (tmrGameClock != null && tmrGameClock.Enabled)
            {
                PauseGameClock();
            }
            else
            {
                ResumeGameClock();
            }
        }

        public override void DialPress(DialPressPayload payload)
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "Dial Push Action");

            if (payload.IsDialPressed)
            {
                Logger.Instance.LogMessage(TracingLevel.INFO, "Dial Pressed");
                dialWasRotated = false;
                return;
            }

            Logger.Instance.LogMessage(TracingLevel.INFO, "Dial Released");
            if (dialWasRotated)
            {
                return;
            }
            if (tmrGameClock != null && tmrGameClock.Enabled)
            {
                PauseGameClock();
            }
            else
            {
                ResumeGameClock();
            }
        }

        public override void DialRotate(DialRotatePayload payload)
        {
            dialWasRotated = true;
            isGameClockRunning = tmrGameClock.Enabled;
            Logger.Instance.LogMessage(TracingLevel.INFO, "Dial Rotated");

            int increment = payload.Ticks * stepSize * -1;                  //adding time to seconds elapsed removes seconds from gameclock; expected add should increase gameclock

            if (payload.IsDialPressed)
            {
                increment *= 15;                                            //if pressed while turning, factor of 15 seconds
            }

            PauseGameClock();
            AdjustGameClock(increment);
            
            if (isGameClockRunning)
            {
                ResumeGameClock();
            }
        }

        public override void TouchPress(TouchpadPressPayload payload)
        {
            if (payload.IsLongPress)
            {
                PauseGameClock();
                ResetCounter();
            }

            return;
        }

        public override void KeyReleased(KeyPayload payload) 
        {
            //track for long press parameters
            keyPressed = false;
            Logger.Instance.LogMessage(TracingLevel.INFO, "Key Released");
        }

        public async override void OnTick()
        {
            long total, minutes, seconds, timeColons;
            long gameSeconds;
            string delimiter = ":", displayClock = String.Empty;
            Dictionary<string, string> displayUpdate = new Dictionary<string, string>();

            //Streamdeck uses this; and is the best place to check long press
            CheckIfResetNeeded();

            //Set Game seconds
            timeColons = Regex.Matches(settings.GameClockTime, ":").Count;

            if (timeColons is 1)
            {
                try
                {
                    string[] timeInput = settings.GameClockTime.Split(':');
                    gameSeconds = Convert.ToInt64(timeInput[0]) * 60 + Convert.ToInt64(timeInput[1]);
                }
                catch
                {
                    Logger.Instance.LogMessage(TracingLevel.INFO, "gameSeconds failed split");
                    gameSeconds = 0;
                }
            }
            else
            {
                Logger.Instance.LogMessage(TracingLevel.INFO, "gameSeconds is not MM:SS");
                gameSeconds = 0;
            }

            
            total = gameClockSeconds;
            gameSeconds = gameSeconds - total;

            if (gameSeconds < 0)
            {
                gameSeconds = 0;
            }

            minutes = gameSeconds / 60;
            seconds = gameSeconds - ( minutes * 60);
            displayClock = $"{minutes.ToString("0")}{delimiter}{seconds.ToString("00")}";

            displayUpdate["title"] = "Game Clock";
            displayUpdate["value"] = $"{displayClock}";
            displayUpdate["indicator"] = Tools.RangeToPercentage((int)gameSeconds, (int)total, 0).ToString();


            // Logger.Instance.LogMessage(TracingLevel.INFO, "gameSeconds " + gameSeconds.ToString("00"));
            // Logger.Instance.LogMessage(TracingLevel.INFO, "minutes " + minutes.ToString("00"));
            // Logger.Instance.LogMessage(TracingLevel.INFO, "seconds " + seconds.ToString("00"));
            SaveInputStringToFile(displayClock);
            await Connection.SetTitleAsync(displayClock);
            await Connection.SetFeedbackAsync(displayUpdate);
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "Received Settings");
            Tools.AutoPopulateSettings(settings, payload.Settings);
            SaveSettings();
        }

        public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) 
        { 
        }

        
        #region Private Methods

        private void ResetCounter()
        {
            gameClockSeconds = 0;                                   //no time elapsed
        }

        private void ResumeGameClock()
        {
            if (tmrGameClock is null)                                //is there a game clock?
            {
                tmrGameClock = new Timer();
                tmrGameClock.Elapsed += TmrGameClock_Elapsed;       //resume from memory
            }
            tmrGameClock.Interval = 1000;                           //every second
            tmrGameClock.Start();                                   //start gameclock
        }
       
        private void TmrGameClock_Elapsed( object sender, ElapsedEventArgs e)
        {
            gameClockSeconds++;
        }

        private void PauseGameClock()
        {
            tmrGameClock.Stop();
        }

        private void AdjustGameClock(int increment)
        {
            gameClockSeconds += increment;
        }

        private void CheckIfResetNeeded()
        {
            if (!keyPressed)
            {
                return;
            }

            if ((DateTime.Now - keyPressStart).TotalSeconds > RESET_COUNTER_KEYPRESS_LENGTH)    //greater than reset length
            {
                PauseGameClock();
                ResetCounter();
            }
        }

        private void SaveInputStringToFile( string outputString)
        {
            try
            {
                //Logger.Instance.LogMessage(TracingLevel.INFO, $"file: {settings.GameClockFile}");
                if (!String.IsNullOrWhiteSpace(settings.GameClockFile))
                {
                    File.WriteAllText(settings.GameClockFile, outputString);
                }
                Connection.ShowOk();
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, $"Error Saving value to file: {settings.GameClockFile} : {ex}");
                Connection.ShowAlert();
                settings.GameClockFile = "ACCESS DENIED";
                SaveSettings();
            }
        }

        private Task SaveSettings()
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "Save Settings executed");
            //Logger.Instance.LogMessage(TracingLevel.INFO, "File Name " + settings.GameClockFile);
            return Connection.SetSettingsAsync(JObject.FromObject(settings));
        }

        #endregion
    }
}