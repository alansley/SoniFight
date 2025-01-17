﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

using DavyKager; // Required for tolk screenreader integration

using au.edu.federation.SoniFight.Properties;

namespace au.edu.federation.SoniFight
{
    static class Program
    {
        // Note: The program will only handle one GameConfig at any given time and this is stored as a public static object in the Form.cs file.

        // Uncomment if we want to attach a console to this application when running as a windows application (see below AttachConsole call)
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        // We need to know whether the game is currently playing a live round or is in a menu.
        // We'll modify this enum based on whether the clock has changed in the last second or not. Live is so, InMenu otherwise.
        // Then, we'll use the gameState enum we'll keep to determine whether we should play triggers based on their GameStateRequirement setting.
        public enum GameState
        {
            InGame,
            InMenu
        };

        // When starting we assume the game is at the menu
        public static GameState gameState = GameState.InMenu;

        // We'll also keep track of the previous (last tick) game state and only play sonification events when the previous and current game states match
        // Note: The reason for this is because between rounds the click gets reset, and without this check we then think we're InGame when we're actually
        //       just between rounds, so it'll trigger InGame sonification between rounds, which we don't particularly want.
        public static GameState previousGameState = GameState.InMenu;

        // DateTime objects to use to determine if an in-game clock tick has occured (used in fighting games)
        static DateTime clockStartTime, clockEndTime;
        
        // Maximum characters to compare when doing string comparisons
        public static int TEXT_COMPARISON_CHAR_LIMIT = 33;

        // Background worker for sonification
        public static BackgroundWorker sonificationBGW = new BackgroundWorker();
        public static AutoResetEvent resetEvent = new AutoResetEvent(false); // We use this to reset the worker

        // Our IrrKlang SoundPlayer instance
        public static SoundPlayer irrKlang = new SoundPlayer();

        // Are we connected to the process specified in the current GameConfig?
        public static bool connectedToProcess = false;

        // Are we currently reconnecting to the process specified in the current GameConfig?
        //public static bool reconnectingToProcess = false;

        // We keep a queue of normal triggers so they can play in the order they came in without overlapping each other and turning into a cacophony
        static Queue<Trigger> normalInGameTriggerQueue = new Queue<Trigger>();

        // Flag to keep track of whether we're running as a 32-bit or 64-bit process
        public static bool is64Bit;

        // Get an instance of the invariant culture so we can override current/currentUI/thread/threadUI cultures with it and compare strings in this manner.
        // Also: Having this means we don't have to insantiate a new instance all the time (like when performing string comparisons) to it'll take load off the garbage collector.
        private static CultureInfo invariantCulture = CultureInfo.InvariantCulture;

        // Flag to keep track of whether there is an active screen reader or not (as determined by Tolk's DetectScreenReader() method which runs in the performSonification method)
        public static bool screenReaderActive;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Set our 64-bit flag depending on whether this is the 32-bit or 64-bit build of the pointer chain tester
            is64Bit = System.Environment.Is64BitProcess;

            // At some point we may wish to have this as a Windows Application (not a Console Application) and attach a console to it.
            // This comes with some caveats like you can't cleanly pipe output to file from it, so I'll leave it for now. In the below
            // AttachConsole call -1 means attach to the parent process, and we also need the native AttachConsole method from kernel32.dll.
            AttachConsole(-1);

            // ----- Culture Localisation -----            
            Console.WriteLine("Original culture           : " + CultureInfo.CurrentCulture.DisplayName);
            Console.WriteLine("Original UI culture        : " + CultureInfo.CurrentUICulture.DisplayName);
            string temp;
            if (CultureInfo.DefaultThreadCurrentCulture == null)   { temp = "null"; } else { temp = CultureInfo.DefaultThreadCurrentCulture.DisplayName;   }
            Console.WriteLine("Original default culture   : " + temp);
            if (CultureInfo.DefaultThreadCurrentUICulture == null) { temp = "null"; } else { temp = CultureInfo.DefaultThreadCurrentUICulture.DisplayName; }
            Console.WriteLine("Original default UI culture: " + temp);
            Console.WriteLine("Attempting culture modification...");

            // Specify current, UI and default threads should all use CultureInfo.InvariantCulture
            // Note: We can force specific cultures via calls to: new CultureInfo("en"), or, new CultureInfo("fr") etc.
            Thread.CurrentThread.CurrentCulture       = invariantCulture; // This thread
            Thread.CurrentThread.CurrentUICulture     = invariantCulture; // This UI thread
            CultureInfo.DefaultThreadCurrentCulture   = invariantCulture; // All future threads            
            CultureInfo.DefaultThreadCurrentUICulture = invariantCulture; // All future UI threads            

            Console.WriteLine("New thread culture    : " + CultureInfo.CurrentCulture.DisplayName);
            Console.WriteLine("New UI culture        : " + CultureInfo.CurrentUICulture.DisplayName);
            Console.WriteLine("New default culture   : " + CultureInfo.DefaultThreadCurrentCulture.DisplayName);
            Console.WriteLine("New default UI culture: " + CultureInfo.DefaultThreadCurrentUICulture.DisplayName);
            Console.WriteLine();

            // Prepare sonficiation background worker...
            sonificationBGW.DoWork += performSonification;      // Specify the work method - this runs when RunWorkerAsync is called
            sonificationBGW.WorkerReportsProgress = false;      // We do not want progress reports
            sonificationBGW.WorkerSupportsCancellation = true;  // We do want to be able to cancel the background worker
            
            // Setup visual styles and rendering context
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            // Run the app, catching any exception that gets raised and always closing down cleanly
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);

                // Get stack trace for the exception with source file information
                var st = new StackTrace(e, true);
                MessageBox.Show(st.ToString());

                // Get the top stack frame
                var frame = st.GetFrame(0);
                MessageBox.Show(frame.ToString());

                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();
                MessageBox.Show(line.ToString());
            }
            finally
            {
                // IrrKlang cleanup (unload and samples and dispose of player)
                irrKlang.ShutDown();
            }            
        }  

        // This method checks for successful comparisons between a trigger and the value read from a watch.
        // Note: Because a trigger may be associated with a number of watches we must provide the index of the watch value we're comparing against.
        public static bool performComparison(Trigger t, dynamic watchValue, int watchIndex)
        {
            // Note: Continuous triggers do NOT call this method because their job is not to compare to a specific value, it's to compare
            //       two values and give a percentage (e.g. player 1 x-location and player 2 x-location).

            // Also: The 'opposite' comparison checks using the previous value below stop multiple retriggers of a sample as the sample only activates
            //       when the value crosses the trigger threshold.

            // Strings used for comparisons
            string stringA = string.Empty;
            string stringB = string.Empty;

            // Guard against user moving to edit tab where triggers are temporarily reset and there is no previous value
            if (t.PreviousValueList.Count > 0)
            {
                // Dynamic type comparisons may possibly fail so wrap 'em in try/catch
                try
                {
                    // What type of value comparison are we making? Deal with each accordingly.
                    switch (t.comparisonType)
                    {
                        case Trigger.ComparisonType.EqualTo:
                            // We'll perform all 'equal-to' comparisons as strings
                            stringA = Convert.ToString(watchValue,invariantCulture);
                            stringB = Convert.ToString(t.Value, invariantCulture);
                            string previousValueString = Convert.ToString(t.PreviousValueList[watchIndex], invariantCulture);

                            // Params: first string, second string, case sensitive, culture for comparison
                            // A result of zero means these strings are identical
                            int previousResult = string.Compare(stringA, previousValueString, false, invariantCulture);
                            if ((string.Compare(stringA, stringB, false, invariantCulture) == 0 || t.triggerType == Trigger.TriggerType.Modifier) &&
                                (previousResult != 0) )
                            {
                                return true; // Strings are identical we have a match
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.LessThan:
                            if ((t.PreviousValueList[watchIndex] >= t.Value || t.triggerType == Trigger.TriggerType.Modifier) && (watchValue < t.Value))
                            {
                                return true;
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.LessThanOrEqualTo:
                            if ((t.PreviousValueList[watchIndex] > t.Value || t.triggerType == Trigger.TriggerType.Modifier) && (watchValue <= t.Value))
                            {
                                return true;
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.GreaterThan:
                            if ((t.PreviousValueList[watchIndex] <= t.Value || t.triggerType == Trigger.TriggerType.Modifier) && (watchValue > t.Value))
                            {
                                return true;
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.GreaterThanOrEqualTo:
                            if ((t.PreviousValueList[watchIndex] < t.Value || t.triggerType == Trigger.TriggerType.Modifier) && (watchValue >= t.Value))
                            {
                                return true;
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.NotEqualTo:
                            if ((t.PreviousValueList[watchIndex] == t.Value || t.triggerType == Trigger.TriggerType.Modifier) && (watchValue != t.Value))
                            {
                                return true;
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.Changed:
                            // NOTE: The changed comparison uses the current watch value and the previous value - it does NOT use the current trigger value, which we
                            //       don't care about.
                            //       Another option we could use in this changed comparison is to say that a change to a blank string does not count as a change.
                            //       Worth thinking about, but I won't put it in just yet.

                            // We'll perform all 'changed' comparisons as strings
                            stringA = Convert.ToString(watchValue, invariantCulture);
                            stringB = Convert.ToString(t.PreviousValueList[watchIndex], invariantCulture);

                            // Params: first string, second string, case sensitive, culture for comparison
                            // A result of zero means these strings are identical
                            if ( string.Compare(stringA, stringB, false, invariantCulture) == 0)
                            {
                                return false; // Same, so have not changed
                            }
                            return true;

                        case Trigger.ComparisonType.Increased:
                            if (t.PreviousValueList[watchIndex] < watchValue || t.triggerType == Trigger.TriggerType.Modifier)
                            {
                                return true;
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.Decreased:
                            if (t.PreviousValueList[watchIndex] > watchValue || t.triggerType == Trigger.TriggerType.Modifier)
                            {
                                return true;
                            }
                            return false; // Comparison failed? Return false.
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(t.Id + " threw as " + Resources.ResourceManager.GetString("dynamicTypeComparisonExceptionString") + e.Message);
                }

            }
            else // If the previous value list count is zero we add this value to the list
            {
                t.PreviousValueList.Add(t.Value);
            }

            // No matches? False it is, then!
            return false;
        }

        // This method checks for a comparison between a dependent trigger's value and a given value. It does NOT check against previous values of that dependent trigger
        // except when the trigger's comparison type is 'changed', 'increased', 'decreased'.
        public static bool performDependentComparison(Trigger t, dynamic watchValue)
        {
            // Note: Normal triggers that make a sound may be co-opted to work as dependent triggers, and we won't stop them.

            // Dynamic type comparisons may possibly fail so wrap 'em in try/catch
            try
            {
                // What type of value comparison are we making? Deal with each accordingly.
                switch (t.comparisonType)
                {
                    case Trigger.ComparisonType.EqualTo:
                        if (watchValue == t.Value)
                        {
                            return true;
                        }
                        return false; // Comparison failed? Return false.

                    case Trigger.ComparisonType.LessThan:
                        if (watchValue < t.Value)
                        {
                            return true;
                        }
                        return false; // Comparison failed? Return false.

                    case Trigger.ComparisonType.LessThanOrEqualTo:
                        if (watchValue <= t.Value)
                        {
                            return true;
                        }
                        return false; // Comparison failed? Return false.

                    case Trigger.ComparisonType.GreaterThan:
                        if (watchValue > t.Value)
                        {
                            return true;
                        }
                        return false; // Comparison failed? Return false.

                    case Trigger.ComparisonType.GreaterThanOrEqualTo:
                        if (watchValue >= t.Value)
                        {
                            return true;
                        }
                        return false; // Comparison failed? Return false.

                    case Trigger.ComparisonType.NotEqualTo:
                        if (watchValue != t.Value)
                        {
                            return true;
                        }
                        return false; // Comparison failed? Return false.

                    case Trigger.ComparisonType.Changed:
                        if (t.PreviousValueList[0] != watchValue)
                        {
                            return true;
                        }
                        return false; // Comparison failed? Return false.

                    case Trigger.ComparisonType.Increased:
                        if (t.PreviousValueList[0] < watchValue)
                        {
                            return true;
                        }
                        return false; // Comparison failed? Return false.

                    case Trigger.ComparisonType.Decreased:
                        if (t.PreviousValueList[0] > watchValue)
                        {
                            return true;
                        }
                        return false; // Comparison failed? Return false.

                } // End of switch block

            }
            catch (Exception e)
            {
                Console.WriteLine(Resources.ResourceManager.GetString("dynamicTypeComparisonExceptionString") + e.Message);
            }

            // No matches? False it is, then!
            return false;
        }
        
        // This is the DoWork method for the sonification BackgroundWorker
        // Note: This worker method is called from the Form1.activate() method.
        public static void performSonification(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            Console.Write("Starting sonification...");

            // Load tolk ready for use
            Tolk.Load();

            // Try to detect a screen reader and set a flag if we find one so we know we can use it for sonification events.
            screenReaderActive = false;
            string screenReaderName = Tolk.DetectScreenReader();
            if (screenReaderName != null)
            {
                screenReaderActive = true;
                Console.WriteLine( Resources.ResourceManager.GetString("tolkActiveString") + screenReaderName);
                if ( Tolk.HasSpeech()  ) { Console.WriteLine( Resources.ResourceManager.GetString("tolkSpeechSupportedString") );  }
                if ( Tolk.HasBraille() ) { Console.WriteLine( Resources.ResourceManager.GetString("tolkBrailleSupportedString") ); }
            }
            else
            {
                Console.WriteLine( Resources.ResourceManager.GetString("tolkNoScreenReaderFoundString") );
            }

            // Save some typing
            GameConfig gc = MainForm.gameConfig;

            // Convert all trigger 'value' properties (which are of type dynamic) to their actual type.
            // Note: This is a one-off operation that we only do at the start before the main sonification loop.
            Trigger t;
            for (int triggerLoop = 0; triggerLoop < gc.triggerList.Count; ++triggerLoop)
            {   

                t = MainForm.gameConfig.triggerList[triggerLoop];

                // As each trigger may be tied to more than one watch we have to loop over them all
                for (int watchIdLoop = 0; watchIdLoop < t.WatchIdList.Count; ++watchIdLoop)
                {
                    try
                    {
                        switch (Utils.getWatchWithId(t.WatchIdList[watchIdLoop]).valueType)
                        {
                            case Watch.ValueType.IntType:
                                t.Value = Convert.ChangeType(t.Value, TypeCode.Int32);
                                t.PreviousValueList.Add(new int());
                                t.PreviousValueList[watchIdLoop] = t.Value; // By value
                                break;
                            case Watch.ValueType.ByteType:
                                t.Value = Convert.ChangeType(t.Value, TypeCode.Byte);
                                t.PreviousValueList.Add(new byte());
                                t.PreviousValueList[watchIdLoop] = t.Value; // By value
                                break;
                            case Watch.ValueType.ShortType:
                                t.Value = Convert.ChangeType(t.Value, TypeCode.Int16);
                                t.PreviousValueList.Add(new short());
                                t.PreviousValueList[watchIdLoop] = t.Value; // By value
                                break;
                            case Watch.ValueType.LongType:
                                t.Value = Convert.ChangeType(t.Value, TypeCode.Int64);
                                t.PreviousValueList.Add(new long());
                                t.PreviousValueList[watchIdLoop] = t.Value; // By value
                                break;
                            case Watch.ValueType.UnsignedIntType:
                                t.Value = Convert.ChangeType(t.Value, TypeCode.UInt32);
                                t.PreviousValueList.Add(new uint());
                                t.PreviousValueList[watchIdLoop] = t.Value; // By value
                                break;
                            case Watch.ValueType.FloatType:
                                t.Value = Convert.ChangeType(t.Value, TypeCode.Single);
                                t.PreviousValueList.Add(new float());
                                t.PreviousValueList[watchIdLoop] = t.Value; // By value
                                break;
                            case Watch.ValueType.DoubleType:
                                t.Value = Convert.ChangeType(t.Value, TypeCode.Double);
                                t.PreviousValueList.Add(new double());
                                t.PreviousValueList[watchIdLoop] = t.Value; // By value
                                break;
                            case Watch.ValueType.BoolType:
                                t.Value = Convert.ChangeType(t.Value, TypeCode.Boolean);
                                t.PreviousValueList.Add(new bool());
                                t.PreviousValueList[watchIdLoop] = t.Value; // By value
                                break;
                            case Watch.ValueType.StringUTF8Type:
                            case Watch.ValueType.StringUTF16Type:
                                t.Value = Convert.ChangeType(t.Value, TypeCode.String);
                                t.PreviousValueList.Add(t.Name);                       // Doesn't matter what string we add here because we immediately overwrite it below
                                t.PreviousValueList[watchIdLoop] = t.Value.ToString(); // Strings are reference types so we create a new copy to ensure value and previousValue don't point to the same thing!
                                break;
                            default:
                                t.Value = Convert.ChangeType(t.Value, TypeCode.Int32);
                                t.PreviousValueList.Add(new int());
                                t.PreviousValueList[watchIdLoop] = t.Value; // By value
                                break;

                        } // End of switch

                    }
                    catch (Exception exception)
                    {
                        MessageBox.Show("Trigger " + t.Id + " failed initial type conversion: " + exception.Message);
                    }

                } // End of loop over watches in trigger watchIdList

            } // End of loop over triggers

            // Get the time and the current clock
            clockStartTime = DateTime.Now;

            // Declare a few vars once here to maintain scope throughout the 'game-loop'
            dynamic watchValue;
            dynamic secondaryWatchValue;
            dynamic currentClock = null;
            dynamic lastClock = null;
            bool foundMatch;
            string now = DateTime.Now.ToString();
            string previousNow = DateTime.Now.ToString();            

            // While SoniFight has not been stopped or closed we'll provide sonification...            
            while (!e.Cancel)
            {
                //Console.WriteLine(DateTime.Now + " - In sonification loop... e.Cancel is: " + e.Cancel);
                now = DateTime.Now.ToString();
                if ( !now.Equals(previousNow) )
                {
                    Console.WriteLine(now);
                    previousNow = now.ToString(); // Overwrite previous contents of now with the current 'now' time
                }

                // Update all active watch destination addresses (this must happen once per poll)
                Watch w;
                for (int watchLoop = 0; watchLoop < gc.watchList.Count; ++watchLoop)
                {
                    w = gc.watchList[watchLoop];

                    // Update the destination address of the watch if it's active - don't bother otherwise.
                    if (w.Active)
                    {
                        w.DestinationAddress = Utils.findFeatureAddress(gc.ProcessHandle, gc.ProcessBaseAddress, w.PointerList);
                    }
                }

                // ----- Process clock trigger to keep track of game state (if there is one) -----                

                // Update the game state to be InGame or InMenu if we have a clock
                if (gc.ClockTriggerId != -1)
                {
                    //Console.WriteLine("Have a clock trigger with id: " + gc.ClockTriggerId);

                    // Grab the clock trigger
                    t = Utils.getTriggerWithId(gc.ClockTriggerId);

                    // Read the value on it
                    if (t.WatchIdList.Count > 0)
                    { 
                        currentClock = Utils.getWatchWithId(t.WatchIdList[0]).getDynamicValueFromType();

                        // Check if a round-tick has passed
                        clockEndTime = DateTime.Now;
                        double elapsedMilliseconds = ((TimeSpan)(clockEndTime - clockStartTime)).TotalMilliseconds;

                        // If a GameConfig clock-tick has has passed (i.e. a second or such)
                        if (elapsedMilliseconds >= MainForm.gameConfig.ClockTickMS)
                        {
                            //Console.WriteLine("A clock tick has passed.");

                            // Reset the start time
                            clockStartTime = DateTime.Now;

                            // Update the previous gamestate
                            Program.previousGameState = Program.gameState;

                            // If the current and last clocks differ...
                            if (currentClock != lastClock)
                            {
                                // ...update the last clock to be the current clock and...
                                lastClock = currentClock;

                                // ...set the current gamestate to be InGame.
                                Program.gameState = GameState.InGame;

                                // This condition check stops us from moving briefly into the InGame state when the clock is reset between rounds or matches
                                if ((currentClock == 0 || lastClock == 0 || currentClock == MainForm.gameConfig.ClockMax) && Program.connectedToProcess)
                                {
                                    Console.WriteLine(Resources.ResourceManager.GetString("suppressedGameStateChangeString") + MainForm.gameConfig.ClockMax);
                                    Program.gameState = GameState.InMenu;

                                    // If the clock is 0 or ClockMax or we've lost connection to the process we clear the normalInGameTriggerQueue
                                    normalInGameTriggerQueue.Clear();
                                }
                            }
                            else // Current and last clock values the same? Then set the gamestate to be InMenu.
                            {
                                Program.gameState = GameState.InMenu;
                            }

                            //Console.WriteLine("Program state is: " + Program.gameState);

                        } // End of if a clock tick has elapsed section

                    } // End of if we have a watch ID for the clock section

                } // End of game state update block

                //else // If gc.ClockTriggerId IS -1 (that is - there is no clock associated with this config)
                //{
                //}

                /* PLEASE NOTE: The below separate normal, continuous and modifier trigger lists are constructed in the GameConfig.connectToProcess method. Also, dependent triggers 
                 *              go into the normal trigger list, but are processed on demand rather than in sequence. 
                 */                 

                // ----- Process continuous triggers -----                

                // If we're InMenu we stop all continuous samples...
                if (Program.gameState == GameState.InMenu)
                {
                    Program.irrKlang.ContinuousEngine.StopAllSounds();
                    Program.irrKlang.PlayingContinuousSamples = false;
                }
                else // ...otherwise we're InGame so start all continuous trigger samples and set our flag to say they're playing.
                {
                    for (int continuousTriggerLoop = 0; continuousTriggerLoop < gc.continuousTriggerList.Count; ++continuousTriggerLoop)
                    {
                        // Grab a trigger
                        t = MainForm.gameConfig.continuousTriggerList[continuousTriggerLoop];

                        // Not already playing? Start the sample!
                        if (!Program.irrKlang.PlayingContinuousSamples)
                        {
                            Program.irrKlang.PlayContinuousSample(t);
                        }

                        // Read the value associated with the watch named by this trigger
                        watchValue = Utils.getWatchWithId(t.WatchIdList[0]).getDynamicValueFromType();

                        // Get the value of the secondary watch associated with this continuous trigger
                        secondaryWatchValue = Utils.getWatchWithId(t.SecondaryIdList[0]).getDynamicValueFromType();

                        // The trigger value acts as the range between watch values for continuous triggers
                        dynamic maxRange = t.Value;

                        // Get the range and make it absolute (i.e. positive)
                        dynamic currentRange = Math.Abs(watchValue - secondaryWatchValue);

                        // Calculate the percentage of the current range to the max range
                        float percentage;

                        /***
                         *  WARNING!
                         *  
                         *  The below switch condition deals with modifying continuous triggers based on whether they should change by volume or pitch, both ascending or descending.
                         *  
                         *  It comes with a VERY IMPORTANT CAVEAT.
                         *  
                         *  If your continuous trigger is varying volume, then you should NOT attach a modifier trigger to it that modifies volume or the results of the modifier trigger
                         *  will be overwritten on the next poll by the calculation of this continuous trigger.
                         *  
                         *  Similarly, if your continuous trigger is varying pitch, then you should NOT attach a modifier trigger to it that modifies pitch or again the result of the
                         *  modifier trigger will be overwritten on the next poll by the calculation of this continuous trigger section.
                         *  
                         *  To reiterate: Continuous changes volume? Modify on pitch only. Continuous changes pitch? Modify on volume only.
                         *  
                         *  Get it? Got it? Good!
                         * 
                         ***/

                        // TODO: Make sure you can't have continuous triggers which use watches with a non-numerical type.

                        // Perform sample volume/rate updates for this continuous trigger
                        switch (t.comparisonType)
                        {
                            case Trigger.ComparisonType.DistanceVolumeDescending:
                                percentage = (float)(currentRange / maxRange);
                                t.CurrentSampleVolume = t.SampleVolume * percentage;
                                t.CurrentSampleSpeed = t.SampleSpeed;
                                Program.irrKlang.ChangeContinuousSampleVolume(t.SampleKey, t.CurrentSampleVolume);
                                break;

                            case Trigger.ComparisonType.DistanceVolumeAscending:
                                percentage = (float)(1.0 - (currentRange / maxRange));
                                t.CurrentSampleVolume = t.SampleVolume * percentage;
                                t.CurrentSampleSpeed = t.SampleSpeed;
                                Program.irrKlang.ChangeContinuousSampleVolume(t.SampleKey, t.CurrentSampleVolume);
                                break;

                            case Trigger.ComparisonType.DistancePitchDescending:
                                percentage = (float)(currentRange / maxRange);
                                t.CurrentSampleSpeed = t.SampleSpeed * percentage;
                                t.CurrentSampleVolume = t.SampleVolume;                                
                                Program.irrKlang.ChangeContinuousSampleSpeed(t.SampleKey, t.CurrentSampleSpeed);
                                break;

                            case Trigger.ComparisonType.DistancePitchAscending:
                                percentage = (float)(1.0 - (currentRange / maxRange));
                                t.CurrentSampleSpeed = t.SampleSpeed * percentage;
                                t.CurrentSampleVolume = t.SampleVolume;
                                //Console.WriteLine("Pitch ascending new speed: " + t.CurrentSampleSpeed + " and volume is: " + t.CurrentSampleVolume);
                                Program.irrKlang.ChangeContinuousSampleSpeed(t.SampleKey, t.CurrentSampleSpeed);
                                break;
                        }

                    } // End of continuous trigger loop

                    // Only once we've set any/all continuous samples to play in the above loop do we set the flag so that multiple copies of the same trigger don't get activated!
                    Program.irrKlang.PlayingContinuousSamples = true;

                } // End of continuous trigger section                

                // ----- Process normal triggers ----- 
                                
                for (int normalTriggerLoop = 0; normalTriggerLoop < gc.normalTriggerList.Count; ++normalTriggerLoop)
                {
                    // Grab a trigger
                    t = MainForm.gameConfig.normalTriggerList[normalTriggerLoop];
                                        
                    // If the allowance state of this trigger doesn't match the game state or we're a dependent trigger then we'll skip to the next trigger.
                    // Note: Dependent triggers get process on demand rather than in this loop over all triggers
                    if ( (t.allowanceType == Trigger.AllowanceType.InGame && Program.gameState == GameState.InMenu) ||
                         (t.allowanceType == Trigger.AllowanceType.InMenu && Program.gameState == GameState.InGame) ||
                          t.triggerType == Trigger.TriggerType.Dependent) 
                    {
                        //Console.WriteLine("Skipping trigger allowance type / game state mismatch."); /* Don't uncomment this - it will FLOOD the console! */
                        continue;
                    }

                    // Initially let's assume a dependent trigger match for this trigger...
                    bool dependentTriggerMatch = true;

                    // ...and then we'll loop over all watches this trigger has.
                    // Note: As each trigger may look at multiple watches, we must compare the trigger value against each watch.
                    for (int watchIdLoop = 0; watchIdLoop < t.WatchIdList.Count; ++watchIdLoop)
                    {
                        //Console.WriteLine("Inside watch loop");

                        // Read the value associated with this watch of this trigger
                        watchValue = Utils.getWatchWithId(t.WatchIdList[watchIdLoop]).getDynamicValueFromType();

                        // Check our trigger for a match with the watch at this index.
                        // NOTE: Even if we're currently playing a normal sample we'll still check for matches and queue any matching triggers.
                        foundMatch = performComparison(t, watchValue, watchIdLoop);

                        // If we found an initial match for this trigger then we'll go further...                        
                        if (foundMatch)
                        {
                            Console.WriteLine("Found match for trigger: " + t.Id + " with value: " + t.Value);

                            // If there is a dependent trigger which isn't -1 and we're on the first watch of this trigger then we'll check all dependent watches.
                            // Note: We only want to check the dependent triggers once per poll of this trigger, not once per watch of this trigger, because once we've found
                            //       out if the dependent trigger conditions are met they either are or are not for this trigger, regardless of how many watches it might have.
                            if (t.SecondaryIdList.Count > 0 && t.SecondaryIdList[0] != -1 && watchIdLoop == 0)
                            {
                                for (int dependentTriggerLoop = 0; dependentTriggerLoop < t.SecondaryIdList.Count; ++dependentTriggerLoop)
                                {
                                    // Get the dependent trigger
                                    Trigger depT = Utils.getTriggerWithId(t.SecondaryIdList[dependentTriggerLoop]);

                                    // Get the dependent trigger's watch
                                    Watch depW = Utils.getWatchWithId(depT.WatchIdList[0]);

                                    // Is the dependency on the dependent trigger met?
                                    dependentTriggerMatch = performDependentComparison(depT, depW.getDynamicValueFromType());

                                    // If we did NOT find a dependent trigger match then we break with that flag as false which means we won't perform the sonification
                                    if (!dependentTriggerMatch)
                                    {
                                        Console.WriteLine("No match on dependent trigger " + depT.Id + " - looking for " + depT.Value + " and got: " + depW.getDynamicValueFromType());
                                        /*Console.WriteLine("Are these values the same?: " + (depT.Value == depW.getDynamicValueFromType()));
                                        Console.WriteLine("Dependent trigger's value is: " + depT.Value + " and previous value is: " + depT.PreviousValueList[0]);*/

                                        //break;
                                    }
                                    else
                                    {
                                        Console.WriteLine("Trigger " + t.Id + " - found dependent match on trigger: " + depT.Id + " with value: " + depT.Value);
                                    }

                                } // End of loop over dependent triggers

                            } // End of is secondary ID list is not -1 and we're not on the first watch in the watch list block

                            // Our trigger matched and any/all dependent triggers also matched so we can proceess the sonification event
                            if (dependentTriggerMatch)
                            {
                                // Gamestate is InGame and allowance type is either InGame or Any?
                                if (Program.gameState == GameState.InGame && t.allowanceType != Trigger.AllowanceType.InMenu)
                                {
                                    // If we're using a screen reader for the sonification event of this trigger...
                                    if (t.UseTolk)
                                    {
                                        if (screenReaderActive)
                                        {
                                            // Substitute all curly braces and braces with number with the watch values
                                            string s = Utils.substituteWatchValuesInString(t, t.SampleFilename, MainForm.gameConfig.ProcessHandle, MainForm.gameConfig.ProcessBaseAddress);
                                            Console.WriteLine(DateTime.Now + " Trigger activated " + t.Id + " " + Resources.ResourceManager.GetString("sayingTolkString") + s);

                                            // ...then say the sample filename text. Final false means queue not interrupt anything currently being spoken.
                                            Tolk.Speak(s, false);
                                        }
                                        else
                                        {
                                            Console.WriteLine(Resources.ResourceManager.GetString("screenReaderNotActiveWarningString") + t.Id);
                                        }

                                    }
                                    else // Audio is file based
                                    {
                                        // if this trigger's allowance type is InMenu we play it immediately, cutting off any existing playing menu audio
                                        if (t.allowanceType == Trigger.AllowanceType.InMenu)
                                        {
                                            Program.irrKlang.PlayMenuSample(t);
                                        }
                                        else // Otherwise the trigger's allowance type must InGame or Any, so we attempt to play it and will queue it if there's already audio playing
                                        {
                                            // Try to play the normal sample. If there's another normal sample playing then this sample will be added to the play queue in the SoundPlayer class.
                                            Program.irrKlang.PlayNormalSample(t);
                                        }
                                    }
                                }
                                else // Game state must be InMenu or allowance type is InMenu or Any - either is fine.
                                {
                                    // If we're using tolk...
                                    if (t.UseTolk)
                                    {
                                        if (screenReaderActive)
                                        {
                                            // Substitute all curly braces and braces with number with the watch values
                                            string s = Utils.substituteWatchValuesInString(t, t.SampleFilename, MainForm.gameConfig.ProcessHandle, MainForm.gameConfig.ProcessBaseAddress);
                                            Console.WriteLine(DateTime.Now + " Trigger activated " + t.Id + " " + Resources.ResourceManager.GetString("sayingTolkString") + s);


                                            if (!string.IsNullOrWhiteSpace(s))
                                            {
                                                // ..then output the sonification event by saying the sample filename string. Final true means interupt any current speech.
                                                Tolk.Speak(s, true);
                                            }
                                        }
                                        else
                                        {
                                            Console.WriteLine(Resources.ResourceManager.GetString("screenReaderNotActiveWarningString") + t.Id);
                                        }
                                    }
                                    else // Audio is file based
                                    {
                                        // Stop any playing samples
                                        Program.irrKlang.StopMenuSounds();

                                        // ...then play the latest trigger sample on the menu engine instance (because the game state is recognised as InMenu)
                                        Program.irrKlang.PlayMenuSample(t);
                                    }

                                } // End of if sonification is via a sample section

                            } // End of found dependent match section. 

                        } // End of found match section

                        // Update our 'previousValue' ready for the next check (used if comparison type is 'Changed').
                        // Note: We do this regardless of whether we found a match                        
                        t.PreviousValueList[watchIdLoop] = watchValue;

                    } // End of loop over watch IDs in watchIDList within each trigger

                    //Console.WriteLine("After watch loop");

                } // End of loop over normal triggers
               
                // ----- Process modifier triggers ----- 

                // No need to reset foundMatch here, it gets overwritten with a new value below!

                for (int modifierTriggerLoop = 0; modifierTriggerLoop < gc.modifierTriggerList.Count; ++modifierTriggerLoop)
                {
                    // Grab a trigger
                    t = MainForm.gameConfig.modifierTriggerList[modifierTriggerLoop];

                    // As each trigger may look at multiple watches, we must compare the trigger value against each watch
                    for (int watchIdLoop = 0; watchIdLoop < t.WatchIdList.Count; ++watchIdLoop)
                    {
                        // Read the new value associated with the watch named by this trigger
                        watchValue = Utils.getWatchWithId(t.WatchIdList[watchIdLoop]).getDynamicValueFromType();

                        // Check our trigger for a match. Final 0 means we're kicking this off at the top level with no recursive trigger dependencies.
                        foundMatch = performComparison(t, Utils.getWatchWithId(t.WatchIdList[watchIdLoop]).getDynamicValueFromType(), 0);

                        // Get the continuous trigger related to this modifier trigger.
                        // Note: We ALWAYS need this because even if we don't find a match, we may need to reset the volume/pitch of the continuous sample to it's non-modified state
                        Trigger continuousTrigger = Utils.getTriggerWithId(t.SecondaryIdList[0]);

                        // Modifier condition met? Okay...
                        if (foundMatch)
                        {
                            // If this modifier trigger is NOT currently active we must activate it because we HAVE found a match for the modifier condition (i.e. foundMatch)
                            if (!t.ModificationActive)
                            {
                                // Set the flag on this modification trigger to say it's active
                                t.ModificationActive = true;

                                // TODO: Localise this output.

                                Console.WriteLine("1--Found modifier match for trigger " + t.Id + " and modification was NOT active.");
                                Console.WriteLine("1--Continuous trigger's current sample volume is: " + continuousTrigger.CurrentSampleVolume);
                                Console.WriteLine("1--Modifier trigger's sample volume is: " + t.SampleVolume);
                                Console.WriteLine("1--Continuous trigger's current sample speed is: " + continuousTrigger.CurrentSampleSpeed);
                                Console.WriteLine("1--Modifier trigger's sample speed is: " + t.SampleSpeed);

                                // Add any volume or pitch changes to the continuous triggers playback
                                continuousTrigger.CurrentSampleVolume *= t.SampleVolume;
                                continuousTrigger.CurrentSampleSpeed *= t.SampleSpeed;
                                Program.irrKlang.ChangeContinuousSampleVolume(continuousTrigger.SampleKey, continuousTrigger.CurrentSampleVolume);
                                Program.irrKlang.ChangeContinuousSampleSpeed(continuousTrigger.SampleKey, continuousTrigger.CurrentSampleSpeed);

                                Console.WriteLine("1--Multiplying gives new volume of: " + continuousTrigger.CurrentSampleVolume + " and speed of: " + continuousTrigger.CurrentSampleSpeed);
                            }

                            // Else modification already active on this continuous trigger? Do nothing.
                        }
                        else // Did NOT match modifier condition. Do we need to reset the continous trigger?
                        {
                            // If this modifier trigger IS currently active and we failed the match we have to reset the continuous triggers playback conditions
                            if (t.ModificationActive)
                            {
                                // TODO: Localise this output.

                                Console.WriteLine("2--Did NOT find modifier match for trigger " + t.Id + " and modification WAS active so needs resetting.");
                                Console.WriteLine("2--Continuous trigger's current sample volume is: " + continuousTrigger.CurrentSampleVolume);
                                Console.WriteLine("2--Modifier trigger's sample volume is: " + t.SampleVolume);
                                Console.WriteLine("2--Continuous trigger's current sample speed is: " + continuousTrigger.CurrentSampleSpeed);
                                Console.WriteLine("2--Modifier trigger's sample speed is: " + t.SampleSpeed);

                                // Set the flag on this modification trigger to say it's inactive
                                t.ModificationActive = false;

                                // Reset the volume and pitch of the continuous trigger based on the modification trigger's volume and pitch
                                continuousTrigger.CurrentSampleVolume /= t.SampleVolume;
                                continuousTrigger.CurrentSampleSpeed /= t.SampleSpeed;
                                Program.irrKlang.ChangeContinuousSampleVolume(continuousTrigger.SampleKey, continuousTrigger.CurrentSampleVolume);
                                Program.irrKlang.ChangeContinuousSampleSpeed(continuousTrigger.SampleKey, continuousTrigger.CurrentSampleSpeed);

                                Console.WriteLine("2--Dividing gives new volume of: " + continuousTrigger.CurrentSampleVolume + " and speed of: " + continuousTrigger.CurrentSampleSpeed);
                            }

                            // Else sonification already inactive after failing match? Do nothing.

                        } // End of if we did NOT match the modifier condition

                    } // End of loop over watches in watchIdList of trigger

                } // End of modifier triggers section

                // --- Pull any normal trigger samples from the queue and play them if we're not already playing a normal sample
                if (!Program.irrKlang.PlayingNormalSample) { Program.irrKlang.PlayQueuedNormalSample(); }

                // Did the user hit the stop button to cancel sonification? In which case do so!
                if (sonificationBGW.CancellationPending && sonificationBGW.IsBusy) { e.Cancel = true; }

                // Update the SoundEngine
                Program.irrKlang.UpdateEngines();

                // Finally, after looping over all triggers we sleep for the amount of time specified in the GameConfig
                Thread.Sleep(MainForm.gameConfig.PollSleepMS);

            } // End of while !e.Cancel

            Console.WriteLine("After sonification while loop");

            // Unload tolk when we're stopping sonification
            Tolk.Unload();

            // If we're here then the background worker must have been cancelled so we call stopSonification
            stopSonification(e);

        } // End of performSonification method

        // Method to stop the sonification background worker
        public static void stopSonification(System.ComponentModel.DoWorkEventArgs e)
        {
            if (e.Cancel)
            {
                Console.WriteLine();
                Console.WriteLine( Resources.ResourceManager.GetString("sonificationStoppedString") );

                GameConfig.processConnectionBGW.CancelAsync();
                GameConfig.processConnectionBGW.Dispose();
                sonificationBGW.CancelAsync();
                //sonificationBGW.Dispose();

                // We do NOT unload all samples here - we only do that on SelectedIndexChanged of the config selection drop-down or on quit.
                // This minimises delay in stopping and starting sonification of the same config.
                // Note: If the user adds new triggers using new samples they will be added to the existing dictionary. 
                //SoundPlayer.UnloadAllSamples();
            }
            else
            {
                Console.WriteLine("Sonification error - stopping. Cause: " + e.Result.ToString());
            }

        } // End of stopSonification method

    } // End of Program class

} // End of namespace
