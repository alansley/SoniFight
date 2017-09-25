﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Windows.Forms;
using System.Globalization;

using DavyKager; // Required for tolk screenreader integration
using au.edu.federation.SoniFight.Properties;

namespace au.edu.federation.SoniFight
{
    static class Program
    {
        // Please note: The program will only handle one GameConfig at any given time and this is
        // stored as a public static object in the Form.cs file.

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

        // DateTime objects to use to determine if one second has passed (at which point we check if the clock has changed)
        static DateTime startTime, endTime;

        // The time that we played our last sonification event
        //static DateTime lastMenuSonificationTime = DateTime.Now;

        // Maximum characters to compare when doing string comparisons
        public static int TEXT_COMPARISON_CHAR_LIMIT = 33;

        // Background worker for sonification
        public static BackgroundWorker sonificationBGW = new BackgroundWorker();
        public static AutoResetEvent resetEvent = new AutoResetEvent(false); // We use this to reset the worker

        // Our IrrKlang SoundPlayer instance
        public static SoundPlayer irrKlang = new SoundPlayer();

        // Are we connected to the process specified in the current GameConfig?
        public static bool connectedToProcess = false;
        
        // We keep a queue of normal triggers so they can play in the order they came in without overlapping each other and turning into a cacophony
        static Queue<Trigger> normalInGameTriggerQueue = new Queue<Trigger>();

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Localisation test code - uncomment to force French localisation etc.
            /*CultureInfo cultureOverride = new CultureInfo("fr");
            Thread.CurrentThread.CurrentUICulture = cultureOverride;
            Thread.CurrentThread.CurrentCulture = cultureOverride;*/

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Prepare sonficiation background worker...
            sonificationBGW.DoWork += performSonification;      // Specify the work method - this runs when RunWorkerAsync is called
            sonificationBGW.WorkerReportsProgress = false;      // We do not want progress reports
            sonificationBGW.WorkerSupportsCancellation = true;  // We do want to be able to cancel the background worker

            // Initialise our irrKlang SoundPlayer class ready to play audio
            //Program.soundplayer = new SoundPlayer();

            // Main loop - we STAY on this line until the application terminates
            Application.Run(new MainForm());

            // IrrKlang cleanup (unload and samples and dispose of player)
            irrKlang.ShutDown();
        }

        // Method to determine if a trigger dependency has been met or not
        private static bool dependenceCheck(Trigger t, int recursiveDepth)
        {
            //Console.WriteLine("Trigger " + t.id + " matched equal with perform comparison on depth of: " + recursiveDepth);

            // No dependent triggers (even if we ARE a dependent trigger at a recursive depth > 0)? Then we've already made a match so return true.
            // Also, if this is a modifier trigger and we've made a value match we'll return true (because modifier triggers are focussed on matching
            // a condition, not only when we pass a threshold!).
            if (t.secondaryId == -1 || t.triggerType == Trigger.TriggerType.Modifier)
            {
                return true;
            }

            // At this point our trigger is a normal trigger. We know this because we return true if the trigger was a modifier, and continuous triggers
            // do not call the performComparison method.    

            // This trigger has a dependent trigger - so we grab it.
            Trigger dependentT = Utils.getTriggerWithId(t.secondaryId);

            // If the dependent trigger is active, then our return type from THIS method is the return from checking the comparison
            // with the dependent trigger within this one (which has already matched or we wouldn't be here). This will recurse as
            // deep as the trigger dependencies are linked - fails after 5 linked dependencies to prevent cyclic dependency crash.
            if (dependentT.active)
            {
                Watch dependentWatch = Utils.getWatchWithId(dependentT.watchOneId);

                // Watch of dependent trigger was not active? Then obviously we must fail as we're not updating the watch details.
                if (!dependentWatch.Active)
                {
                    return false;
                }

                // Does the dependent trigger match its target condition?
                bool dependentResult = performComparison(dependentT, dependentWatch.getDynamicValueFromType(), recursiveDepth + 1);

                // No? Then provide feedback that we'll be supressing this trigger because its dependent trigger failed.
                if (!dependentResult)
                {   
                    string s1 = Resources.ResourceManager.GetString("triggerWithTrailingSpaceString");
                    string s2 = Resources.ResourceManager.GetString("suppressedAsDependentString");
                    string s3 = Resources.ResourceManager.GetString("failedDepthString");
                    Console.WriteLine(s1 + t.id + s2 + dependentT.id + s3 + recursiveDepth);
                }
                return dependentResult;
            }
            else // Dependent trigger was not active so dependency fails and we record no-match as the end result.
            {
                return false;
            }
        }

        // This method checks for successful comparisons between a trigger and the value read from that triggers watch        
        public static bool performComparison(Trigger t, dynamic readValue, int recursiveDepth)
        {
            // Note: Continuous triggers do NOT call this method because their job is not to compare to a specific value, it's to compare
            //       two values and give a percentage (e.g. player 1 x-location and player 2 x-location).

            // Don't recurse more than 5 levels (so 6 in total, also stops cyclic loop stack overflow)
            if (recursiveDepth >= 5)
            {
                return false;
            }

            // Note: The 'opposite' comparison checks using the previous value below stop multiple retriggers of a sample as the sample only activates
            //       when the value crosses the trigger threshold.

            // Guard against user moving to edit tab where triggers are temporarily reset and there is no previous value
            if (t.previousValue != null)
            {
                // Dynamic type comparisons may possibly fail so wrap 'em in try/catch
                try
                {
                    // What type of value comparison are we making? Deal with each accordingly.
                    switch (t.comparisonType)
                    {
                        case Trigger.ComparisonType.EqualTo:
                            if ((t.previousValue != t.value || recursiveDepth > 0 || t.triggerType == Trigger.TriggerType.Modifier) && (readValue == t.value))
                            {
                                return dependenceCheck(t, recursiveDepth);
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.LessThan:
                            if ((t.previousValue > t.value || recursiveDepth > 0 || t.triggerType == Trigger.TriggerType.Modifier) && (readValue < t.value))
                            {
                                return dependenceCheck(t, recursiveDepth);
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.LessThanOrEqualTo:
                            if ((t.previousValue > t.value || recursiveDepth > 0 || t.triggerType == Trigger.TriggerType.Modifier) && (readValue <= t.value))
                            {
                                return dependenceCheck(t, recursiveDepth);
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.GreaterThan:
                            if ((t.previousValue < t.value || recursiveDepth > 0 || t.triggerType == Trigger.TriggerType.Modifier) && (readValue > t.value))
                            {
                                return dependenceCheck(t, recursiveDepth);
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.GreaterThanOrEqualTo:
                            if ((t.previousValue < t.value || recursiveDepth > 0 || t.triggerType == Trigger.TriggerType.Modifier) && (readValue >= t.value))
                            {
                                return dependenceCheck(t, recursiveDepth);
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.NotEqualTo:
                            if ((t.previousValue == t.value || recursiveDepth > 0 || t.triggerType == Trigger.TriggerType.Modifier) && (readValue != t.value))
                            {
                                return dependenceCheck(t, recursiveDepth);
                            }
                            return false; // Comparison failed? Return false.

                        case Trigger.ComparisonType.Changed:
                            if (readValue != t.previousValue || recursiveDepth > 0 || t.triggerType == Trigger.TriggerType.Modifier)
                            {
                                return dependenceCheck(t, recursiveDepth);
                            }
                            return false; // Comparison failed? Return false.
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine( Resources.ResourceManager.GetString("dynamicTypeComparisonExceptionString") + e.Message);
                }

            } // End of it t.previousValue != null block

            // No matches? False it is, then!
            return false;
        }

        
        // This is the DoWork method for the sonification BackgroundWorker
        public static void performSonification(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            // Load tolk library ready for use
            Tolk.Load();

            // Try to detect a screen reader and set a flag if we find one so we know we can use it for sonification events.
            bool screenReaderActive = false;
            string screenReaderName = Tolk.DetectScreenReader();
            if (screenReaderName != null)
            {
                screenReaderActive = true;
                Console.WriteLine( Resources.ResourceManager.GetString("tolkActiveString") + screenReaderName);
                if ( Tolk.HasSpeech() )
                {
                    Console.WriteLine( Resources.ResourceManager.GetString("tolkSpeechSupportedString") );
                }
                if ( Tolk.HasBraille() )
                {
                    Console.WriteLine( Resources.ResourceManager.GetString("tolkBrailleSupportedString") );
                }
            }
            else
            {
                Console.WriteLine( Resources.ResourceManager.GetString("tolkNoScreenReaderFoundString") );
            }

            // Save some typing
            GameConfig gc = MainForm.gameConfig;

            // Convert all trigger 'value' properties (which are of type dynamic) to their actual type
            // Note: This is a ONE-OFF operation that we only do at the start before the main sonification loop
            Trigger t;
            for (int triggerLoop = 0; triggerLoop < gc.triggerList.Count; ++triggerLoop)
            {
                t = MainForm.gameConfig.triggerList[triggerLoop];

                switch (Utils.getWatchWithId(t.watchOneId).valueType)
                {
                    case Watch.ValueType.IntType:
                        t.value = Convert.ChangeType(t.value, TypeCode.Int32);
                        t.previousValue = new int();
                        t.previousValue = t.value; // By value
                        break;
                    case Watch.ValueType.ShortType:
                        t.value = Convert.ChangeType(t.value, TypeCode.Int16);
                        t.previousValue = new short();
                        t.previousValue = t.value; // By value
                        break;
                    case Watch.ValueType.LongType:
                        t.value = Convert.ChangeType(t.value, TypeCode.Int64);
                        t.previousValue = new long();
                        t.previousValue = t.value; // By value
                        break;
                    case Watch.ValueType.FloatType:
                        t.value = Convert.ChangeType(t.value, TypeCode.Single);
                        t.previousValue = new float();
                        t.previousValue = t.value; // By value
                        break;
                    case Watch.ValueType.DoubleType:
                        t.value = Convert.ChangeType(t.value, TypeCode.Double);
                        t.previousValue = new double();
                        t.previousValue = t.value; // By value
                        break;
                    case Watch.ValueType.BoolType:
                        t.value = Convert.ChangeType(t.value, TypeCode.Boolean);
                        t.previousValue = new bool();
                        t.previousValue = t.value; // By value
                        break;
                    case Watch.ValueType.StringUTF8Type:
                    case Watch.ValueType.StringUTF16Type:
                        t.value = Convert.ChangeType(t.value, TypeCode.String);
                        t.previousValue = t.value.ToString(); // Strings are reference types so we create a new copy to ensure value and previousValue don't point to the same thing!
                        break;
                    default:
                        t.value = Convert.ChangeType(t.value, TypeCode.Int32);
                        t.previousValue = new int();
                        t.previousValue = t.value; // By value
                        break;
                }

            } // End of loop over triggers

            // Get the time and the current clock
            startTime = DateTime.Now;

            // Declare a few vars once here to maintain scope throughout the 'game-loop'
            dynamic readValue;
            dynamic readValue2;
            dynamic currentClock = null;
            dynamic lastClock = null;
            bool foundMatch;

            // While we are providing sonification...            
            while (!e.Cancel)
            {
                //Console.WriteLine("Game state is: " + gameState);
                
                // Update all active watch destination addresses (this must happen once per poll)
                Watch w;
                for (int watchLoop = 0; watchLoop < gc.watchList.Count; ++watchLoop)
                {
                    w = gc.watchList[watchLoop];

                    // Update the destination address of the watch if it's active - don't bother otherwise.
                    if (w.Active)
                    {
                        w.updateDestinationAddress(gc.ProcessHandle, gc.ProcessBaseAddress);
                    }
                }

                // ----- Process clock trigger to keep track of game state (if there is one) -----                

                // Update the game state to be InGame or InMenu if we have a clock
                if (gc.ClockTriggerId != -1)
                {
                    // Grab the clock trigger
                    t = Utils.getTriggerWithId(gc.ClockTriggerId);

                    // Read the value on it
                    currentClock = Utils.getWatchWithId(t.watchOneId).getDynamicValueFromType();

                    // Check if a round-tick has passed
                    endTime = DateTime.Now;
                    double elapsedMilliseconds = ((TimeSpan)(endTime - startTime)).TotalMilliseconds;

                    // If a GameConfig clock-tick has has passed (i.e. a second or such)
                    if (elapsedMilliseconds >= MainForm.gameConfig.ClockTickMS)
                    {
                        //Console.WriteLine("A clock tick has passed.");

                        // Reset the start time
                        startTime = DateTime.Now;

                        // Update the previous gamestate
                        Program.previousGameState = Program.gameState;

                        // If the current and last clocks differ...
                        if (currentClock != lastClock)
                        {
                            // ...update the last clock to be the current clock and...
                            lastClock = currentClock;

                            // ...set the current gamestate to be InGame.
                            Program.gameState = GameState.InGame;
                            //Console.WriteLine("GameState is InGame.");

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

                    } // End of if a second or more has elapsed block                    

                } // End of game state update block               
                                

                /*** NOTE: The below separate trigger lists are constructed in the GameConfig.connectToProcess method ***/
                 

                // ----- Process continuous triggers -----                

                // If we're InMenu we stop all continuous samples...
                if (Program.gameState == GameState.InMenu)
                {
                    Program.irrKlang.ContinuousEngine.StopAllSounds();
                    Program.irrKlang.PlayingContinuousSamples = false;
                }
                else // ...otherwise we're InGame so start them ALL and set our flag to say continuous samples are playing.
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
                        readValue = Utils.getWatchWithId(t.watchOneId).getDynamicValueFromType();

                        // Get the secondary watch associated with this continuous trigger
                        readValue2 = Utils.getWatchWithId(t.secondaryId).getDynamicValueFromType();

                        // The trigger value acts as the range between watch values for continuous triggers
                        dynamic maxRange = t.value;

                        // Get the range and make it absolute (i.e. positive)
                        dynamic currentRange = Math.Abs(readValue - readValue2);

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
                                t.currentSampleVolume = t.sampleVolume * percentage;
                                Program.irrKlang.ChangeContinuousSampleVolume(t.sampleKey, t.currentSampleVolume);
                                break;

                            case Trigger.ComparisonType.DistanceVolumeAscending:
                                percentage = (float)(1.0 - (currentRange / maxRange));
                                t.currentSampleVolume = t.sampleVolume * percentage;
                                Program.irrKlang.ChangeContinuousSampleVolume(t.sampleKey, t.currentSampleVolume);
                                break;

                            case Trigger.ComparisonType.DistancePitchDescending:
                                percentage = (float)(currentRange / maxRange);
                                t.currentSampleSpeed = t.sampleSpeed * percentage;
                                Program.irrKlang.ChangeContinuousSampleSpeed(t.sampleKey, t.currentSampleSpeed);
                                break;

                            case Trigger.ComparisonType.DistancePitchAscending:
                                percentage = (float)(1.0 - (currentRange / maxRange));
                                t.currentSampleSpeed = t.sampleSpeed * percentage;                                
                                Program.irrKlang.ChangeContinuousSampleSpeed(t.sampleKey, t.currentSampleSpeed);
                                break;
                        }

                    } // End of continuous trigger loop

                    // Only once we've set any/all continuous samples to play in the above loop do we set the flag so that multiple copies of the same trigger don't get activated!
                    Program.irrKlang.PlayingContinuousSamples = true;

                } // End of continuous trigger section


                // ----- Process normal triggers ----- 

                // Initially say that we have not found a match to activate a sonification event
                //foundMatch = false;

                for (int normalTriggerLoop = 0; normalTriggerLoop < gc.normalTriggerList.Count; ++normalTriggerLoop)
                {
                    // Grab a trigger
                    t = MainForm.gameConfig.normalTriggerList[normalTriggerLoop];

                    // Read the new value associated with the watch named by this trigger
                    readValue = Utils.getWatchWithId(t.watchOneId).getDynamicValueFromType();

                    // Check our trigger for a match. Final 0 means we're kicking this off at the top level with no recursive trigger dependencies.
                    // NOTE: Even if we're currently playing a normal sample we'll still check for matches and queue any matching triggers.
                    foundMatch = performComparison(t, Utils.getWatchWithId(t.watchOneId).getDynamicValueFromType(), 0);
                    
                    // If we found a match...
                    if (foundMatch)
                    {
                        // Gamestate is InGame and allowance type is either InGame or Any?
                        if (Program.gameState == GameState.InGame && t.allowanceType != Trigger.AllowanceType.InMenu)
                        {
                            // If we're using a screen reader for the sonification event of this trigger...
                            if (screenReaderActive && t.useTolk)
                            {
                                // ...then say the sample filename text. Final false means queue not interrupt anything currently being spoken.
                                Tolk.Speak(t.sampleFilename, false);
                            }
                            else // Sample is file based
                            {
                                // Don't attempt to 'say' the sample name
                                if (!t.useTolk)
                                {
                                    if (t.allowanceType == Trigger.AllowanceType.InMenu)
                                    {
                                        Program.irrKlang.PlayMenuSample(t);
                                    }
                                    else // Allowance type is InGame or Any
                                    {
                                        // Try to play the normal sample. If there's another normal sample playing then this sample will be added to the play queue in the SoundPlayer class.
                                        Program.irrKlang.PlayNormalSample(t);
                                    }
                                }

                            } // End of if this trigger is a sample (not a screen reader based event) block
                        }
                        else // Game state must be InMenu or allowance type is InMenu - either is fine.
                        {
                            // If we're using tolk and have an active screen reader...
                            if (t.useTolk && screenReaderActive)
                            {
                                // ..then output the sonification event by saying the sample filename string. Final true means interupt any current speech.
                                Tolk.Speak(t.sampleFilename, true);
                            }
                            else // Sample is a sample file as opposed to screen reader output
                            {
                                if (!t.useTolk)
                                {
                                    if (Program.gameState == GameState.InMenu && t.allowanceType != Trigger.AllowanceType.InGame) // i.e. allowance type is InMenu or Any
                                    {
                                        // Stop any playing samples
                                        Program.irrKlang.StopMenuSounds();                                        

                                        // ...then play the latest trigger sample.
                                        Program.irrKlang.PlayMenuSample(t);
                                    }
                                    else // Allowance type must be Any
                                    {

                                    }
                                }
                            }

                        } // End of if sonification is via a sample section

                    } // End of found match section. 

                    // Update our 'previousValue' ready for the next check (used if comparison type is 'Changed').
                    // Note: We do this regardless of whether we found a match
                    t.previousValue = readValue;

                } // End of loop over normal triggers

                /*
                // There are other conditions under which we can skip processing triggers - such as...
                if ( (t.allowanceType == Trigger.AllowanceType.InGame && Program.gameState == GameState.InMenu)  ||  // ...if the trigger allowance and game state don't match...
                     (t.allowanceType == Trigger.AllowanceType.InMenu && Program.gameState == GameState.InGame)  ||  // ...both ways, or... 
                     (Program.gameState != Program.previousGameState)                                            ||  // ...if we haven't been in this game state for 2 'ticks' or...
                     (t.isClock)                                                                                 ||  // ...if this is the clock trigger or...
                     (!t.active) )                                                                                   // ...if the trigger is not active.
                {
                    // Skip the rest of the loop for this trigger
                    continue;
                }
                */



                // ----- Process modifier triggers ----- 

                // No need to reset foundMatch here, it gets overwritten with a new value below!

                for (int modifierTriggerLoop = 0; modifierTriggerLoop < gc.modifierTriggerList.Count; ++modifierTriggerLoop)
                {
                    // Grab a trigger
                    t = MainForm.gameConfig.modifierTriggerList[modifierTriggerLoop];

                    // Read the new value associated with the watch named by this trigger
                    readValue = Utils.getWatchWithId(t.watchOneId).getDynamicValueFromType();

                    // Check our trigger for a match. Final 0 means we're kicking this off at the top level with no recursive trigger dependencies.
                    foundMatch = performComparison(t, Utils.getWatchWithId(t.watchOneId).getDynamicValueFromType(), 0);
                    
                    // Get the continuous trigger related to this modifier trigger.
                    // Note: We ALWAYS need this because even if we don't find a match, we may need to reset the volume/pitch of the continuous sample to it's non-modified state
                    Trigger continuousTrigger = Utils.getTriggerWithId(t.secondaryId);
                    
                    // Modifier condition met? Okay...
                    if (foundMatch)
                    {
                        // If this modifier trigger is NOT currently active we must activate it because we HAVE found a match for the modifier condition (i.e. foundMatch)
                        if (!t.modificationActive)
                        {
                            // Set the flag on this modification trigger to say it's active
                            t.modificationActive = true;

                            // TODO: Localise this output.

                            /*Console.WriteLine("1--Found modifier match for trigger " + t.id + " and modification was NOT active.");
                            Console.WriteLine("1--Continuous trigger's current sample volume is: " + continuousTrigger.currentSampleVolume);
                            Console.WriteLine("1--Modifier trigger's sample volume is: " + t.sampleVolume);
                            Console.WriteLine("1--Continuous trigger's current sample speed is: " + continuousTrigger.currentSampleSpeed);
                            Console.WriteLine("1--Modifier trigger's sample speed is: " + t.sampleSpeed);*/

                            // Add any volume or pitch changes to the continuous triggers playback
                            continuousTrigger.currentSampleVolume *= t.sampleVolume;
                            continuousTrigger.currentSampleSpeed *= t.sampleSpeed;
                            Program.irrKlang.ChangeContinuousSampleVolume(continuousTrigger.sampleKey, continuousTrigger.currentSampleVolume);
                            Program.irrKlang.ChangeContinuousSampleSpeed(continuousTrigger.sampleKey, continuousTrigger.currentSampleSpeed);

                            //Console.WriteLine("1--Multiplying gives new volume of: " + continuousTrigger.currentSampleVolume + " and speed of: " + continuousTrigger.currentSampleSpeed);
                        }

                        // Else modification already active on this continuous trigger? Do nothing.
                    }
                    else // Did NOT match modifier condition. Do we need to reset the continous trigger?
                    {
                        // If this modifier trigger IS currently active and we failed the match we have to reset the continuous triggers playback conditions
                        if (t.modificationActive)
                        {
                            // TODO: Localise this output.

                            /*Console.WriteLine("2--Did NOT find modifier match for trigger " + t.id + " and modification WAS active so needs resetting.");
                            Console.WriteLine("2--Continuous trigger's current sample volume is: " + continuousTrigger.currentSampleVolume);
                            Console.WriteLine("2--Modifier trigger's sample volume is: " + t.sampleVolume);
                            Console.WriteLine("2--Continuous trigger's current sample speed is: " + continuousTrigger.currentSampleSpeed);
                            Console.WriteLine("2--Modifier trigger's sample speed is: " + t.sampleSpeed);*/

                            // Set the flag on this modification trigger to say it's inactive
                            t.modificationActive = false;

                            // Reset the volume and pitch of the continuous trigger based on the modification trigger's volume and pitch
                            continuousTrigger.currentSampleVolume /= t.sampleVolume;
                            continuousTrigger.currentSampleSpeed /= t.sampleSpeed;
                            Program.irrKlang.ChangeContinuousSampleVolume(continuousTrigger.sampleKey, continuousTrigger.currentSampleVolume);
                            Program.irrKlang.ChangeContinuousSampleSpeed(continuousTrigger.sampleKey, continuousTrigger.currentSampleSpeed);

                            //Console.WriteLine("2--Dividing gives new volume of: " + continuousTrigger.currentSampleVolume + " and speed of: " + continuousTrigger.currentSampleSpeed);
                        }

                        // Else sonification already inactive after failing match? Do nothing.

                    } // End of if we did NOT match the modifier condition

                } // End of modifier triggers section


                // ----- Process menu triggers -----
                /*
                if (Program.gameState == GameState.InMenu)
                { 
                    for (int menuTriggerLoop = 0; menuTriggerLoop < gc.menuTriggerList.Count; ++menuTriggerLoop)
                    {
                        // Grab a trigger
                        t = MainForm.gameConfig.menuTriggerList[menuTriggerLoop];

                        // Read the new value associated with the watch named by this trigger
                        readValue = Utils.getWatchWithId(t.watchOneId).getDynamicValueFromType();

                        // Check our trigger for a match. Final 0 means we're kicking this off at the top level with no recursive trigger dependencies.
                        // NOTE: Even if we're currently playing a normal sample we'll still check for matches and queue any matching triggers.
                        foundMatch = performComparison(t, Utils.getWatchWithId(t.watchOneId).getDynamicValueFromType(), 0);

                        // If we found a match...
                        if (foundMatch)
                        {   
                            // If we're using a screen reader for the sonification event of this trigger...
                            if (screenReaderActive && t.useTolk)
                            {
                                // ...then the sample filename contains the text to say - so say it. Final truee means interrupt anything currently being spoken.
                                Tolk.Speak(t.sampleFilename, true);
                            }
                            else // Sample is file based
                            {
                                // Don't attempt to 'say' the sample name
                                if (!t.useTolk)
                                {
                                    // Stop any playing samples
                                    Program.irrKlang.StopMenuSounds();

                                    // Print some debug useful for fine-tuning configs
                                    Console.WriteLine(Resources.ResourceManager.GetString("inMenuSampleString") + t.sampleFilename +
                                                      Resources.ResourceManager.GetString("triggerIdString") + t.id +
                                                      Resources.ResourceManager.GetString("volumeString") + t.sampleVolume +
                                                      Resources.ResourceManager.GetString("speedString") + t.sampleSpeed);

                                    // ...then play the sample.
                                    Program.irrKlang.PlayMenuSample(t);
                                }

                            } // End of if this trigger is a sample (not a screen reader based event) block

                        } // End of found match section. 

                        // Update our 'previousValue' ready for the next check (used if comparison type is 'Changed').
                        // Note: We do this regardless of whether we found a match
                        t.previousValue = readValue;

                    } // End of loop over menu triggers

                } // End of if gamestate is InMenu block

                */

                // --- Pull normal sample from the queue and play it if we're not already playing a normal sample

                if (!Program.irrKlang.PlayingNormalSample)
                {
                    Program.irrKlang.PlayQueuedNormalSample();
                }
                
                    

                //} // End of trigger sonification loop

                /*

                if ((normalInGameTriggerQueue.Count > 0) 
                {
                    t = normalInGameTriggerQueue.Dequeue();

                   

                    // Now print some debug useful for fine-tuning configs...
                    Console.WriteLine("BY JOVE!!!! "  + Resources.ResourceManager.GetString("inGameSampleString") + t.sampleFilename +
                                      Resources.ResourceManager.GetString("triggerIdString") + t.id +
                                      Resources.ResourceManager.GetString("volumeString") + t.sampleVolume +
                                      Resources.ResourceManager.GetString("speedString") + t.sampleSpeed);

                    // ...and finally play the sample for this trigger. This will either be the trigger we just matched the
                    // condition for, or the next queued normal InGame trigger if there was one.
                    //SoundPlayer.PlayQueueableSample(t.sampleKey, t.sampleVolume, t.sampleSpeed, false); // Final false is because normal triggers don't loop
                    Program.irrKlang.PlayNormalSample(t);
                }*/

                    /*
                    // Now print some debug useful for fine-tuning configs...
                    Console.WriteLine(Resources.ResourceManager.GetString("inGameSampleString") + t.sampleFilename +
                                      Resources.ResourceManager.GetString("triggerIdString") + t.id +
                                      Resources.ResourceManager.GetString("volumeString") + t.sampleVolume +
                                      Resources.ResourceManager.GetString("speedString") + t.sampleSpeed);

                    

                    // Also, set that we're now playing a normal InGame trigger so that any further matches during this poll get added
                    // to the normalInGameTriggerQueue rather than played immediately.
                    currentlyPlayingQueueableTrigger = true;

                    //continue;
                    */
            
                                            /*
                                            else // If we ARE currently playing a normal in-game trigger sample...
                                            {
                    // ...then we just add this one to the queue for when the currently playing sample ends which will
                    // activate the above block to pull the next trigger from the queue during the next poll loop
                    normalInGameTriggerQueue.Enqueue(t);

                    Console.WriteLine(DateTime.Now + " - 666 Adding trigger. New queue length is: " + normalInGameTriggerQueue.Count + " with last addition: " + t.sampleFilename);
                    */
            

                // Do a final check to see if there's a queued normal InGame trigger to play.
                // NOTE: Without this section the queue builds up and doesn't get emptied properly.
                /*if (normalInGameTriggerQueue.Count > 0)
                {
                    // Are we already playing a normal InGame trigger?
                    currentlyPlayingNormalInGameTrigger = SoundPlayer.PlayingNormalInGameTrigger(gc.triggerList);

                    // If not then...
                    if (!currentlyPlayingNormalInGameTrigger)
                    {
                        Console.WriteLine("555We can play a queued trigger.");

                        // ...and get the next queued trigger from the from of the line.
                        t = normalInGameTriggerQueue.Dequeue();

                        Console.Write("Playing queued sample - ");

                        // Now print some debug useful for fine-tuning configs...
                        Console.WriteLine(Resources.ResourceManager.GetString("inGameSampleString") + t.sampleFilename +
                                          Resources.ResourceManager.GetString("triggerIdString") + t.id +
                                          Resources.ResourceManager.GetString("volumeString") + t.sampleVolume +
                                          Resources.ResourceManager.GetString("speedString") + t.sampleSpeed);

                        // ...and finally play the sample for this trigger
                        SoundPlayer.Play(t.sampleKey, t.sampleVolume, t.sampleSpeed, false); // Final false is because normal triggers don't loop
                    }

                } // End of final if there's a queued trigger block
                */

                // Did the user hit the stop button to cancel sonification? In which case do so!
                if (sonificationBGW.CancellationPending && sonificationBGW.IsBusy)
                {
                    e.Cancel = true;
                }

                // Update the SoundEngine
                Program.irrKlang.UpdateEngines();

                // Finally, after looping over all triggers we sleep for the amount of time specified in the GameConfig
                Thread.Sleep(MainForm.gameConfig.PollSleepMS);

            } // End of while !e.Cancel

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
                sonificationBGW.Dispose();

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
