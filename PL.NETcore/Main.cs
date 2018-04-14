/*-----------------------------------------------------------------------------------------

  C#Prolog -- Copyright (C) 2007-2014 John Pool -- j.pool@ision.nl

  This library is free software; you can redistribute it and/or modify it under the terms of
  the GNU General Public License as published by the Free Software Foundation; either version
  2 of the License, or any later version.

  This library is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
  without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
  See the GNU General Public License for details, or enter 'license.' at the command prompt.

-------------------------------------------------------------------------------------------*/

using System;
using System.Text;

namespace Prolog
{
  class PrologParser
  {
    [STAThread]
    public static void Main (string [] args)
    {
      PrologEngine e = null;

      try
      {
        e = new PrologEngine (new DosIO ());

        // ProcessArgs -- for batch processing. Can be left out if not used
        //if (e.ProcessArgs (args, false)) return;

        SetPreferredConsoleProperties (e);
        Console.Title = "C#Prolog command window";
        Console.WriteLine (PrologEngine.IntroText);
        Console.WriteLine ("\r\n--- Enter !! for command history, help for a list of all commands");

        //if (Engine.ConfigSettings.InitialConsultFile != null)   // set in CSProlog.exe.config
        //  e.Consult (Engine.ConfigSettings.InitialConsultFile); // any additional initialisations

        while (!e.Halted)
        {
          Console.Write (e.Prompt);
          e.Query = ReadQuery ();

          // Use e.GetFirstSolution instead of the loop below if you want the first solution only.
          //Console.Write (e.GetFirstSolution (e.Query));

          foreach (PrologEngine.ISolution s in e.SolutionIterator)
          {
            // In order to get the individual variables:
            //foreach (Engine.IVarValue varValue in s.VarValuesIterator)
            // { Console.WriteLine (varValue.Value.To<int> ()); } // or ToString () etc.
            Console.Write (s);

            if (s.IsLast || !UserWantsMore ()) break;
          }

          Console.WriteLine ();
        }
      }
      catch (Exception x)
      {
        Console.WriteLine ("Error while initializing Prolog Engine. Message was:\r\n{0}",
          x.GetBaseException ().Message + Environment.NewLine + x.StackTrace);
        Console.ReadLine ();
      }
      finally
      {
        if (e != null) e.PersistCommandHistory (); // cf. CSProlog.exe.config
      }
    }


    #region Console I/O
    static void SetPreferredConsoleProperties (PrologEngine e)
    {
      Console.ForegroundColor = ConsoleColor.DarkBlue;
      Console.BackgroundColor = ConsoleColor.White;
      Console.Clear (); // applies the background color to the *entire* window background
      try{Console.WindowWidth = 140;}catch{ Console.Write("0"); }//linux doesn't support this
      try{Console.WindowHeight = 60;}catch{ Console.Write("1"); }//linux doesn't support this
      try{Console.BufferWidth = 140;}catch{ Console.Write("2");}//linux doesn't support this
      try{Console.BufferHeight = 3000;}catch{ Console.Write("3"); }//linux doesn't support this
      try{Console.WindowTop = 0;}catch{  Console.Write("4"); }//linux doesn't support this
      try{Console.WindowLeft = 0;}catch{  Console.Write("5"); }//linux doesn't support this
      
      // The following line prevents ^C from exiting the application
      Console.CancelKeyPress += new ConsoleCancelEventHandler (e.Console_CancelKeyPress);
    }

    static string ReadQuery ()
    {
      StringBuilder sb = new StringBuilder ();
      string line;

      while (true)
      {
        if ((line = Console.ReadLine ()) == null)
        {
          sb.Length = 0;

          break;
        }
        else
        {
          sb.AppendLine (line);

          if (line.EndsWith ("/") || line.StartsWith ("!") || line.EndsWith (".")) break;

          Console.Write ("|  ");
        }
      }

      return sb.ToString ();
    }


    static bool UserWantsMore ()
    {
      Console.Write ("  more? (y/n) ");
      char response = Console.ReadKey ().KeyChar;

      if (response == 'y' || response == ';')
      {
        Console.WriteLine ();

        return true;
      }

      return false;
    }
    #endregion
  }
}
