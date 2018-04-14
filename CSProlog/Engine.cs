/*-----------------------------------------------------------------------------------------

  C#Prolog -- Copyright (C) 2007-2015 John Pool -- j.pool@ision.nl

  This library is free software; you can redistribute it and/or modify it under the terms of
  the GNU Lesser General Public License as published by the Free Software Foundation; either 
  version 3.0 of the License, or any later version.

  This library is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
  without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
  See the GNU Lesser General Public License (http://www.gnu.org/licenses/lgpl-3.0.html), or 
  enter 'license' at the command prompt.

-------------------------------------------------------------------------------------------*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace Prolog
{
#if NETSTANDARD
    using ApplicationException = Exception;
    using Stack = Stack<object>;
#endif

  #region Exceptions
  enum PrologException { ioException }

  public class AbortQueryException : ApplicationException
  {
    public AbortQueryException () : base ("\r\n  Execution terminated by user") { }
  }
  #endregion Exceptions

  #region Engine
  public partial class PrologEngine
  {
    static string IOException = "ioException";
    //static string XmlException = "xmlException";

    #region ChoicePoint
    public class ChoicePoint
    {
      protected TermNode goalListHead;
      protected ClauseNode nextClause; // next clause to be tried for goalListHead
      protected bool active;
      public TermNode GoalListHead { get { return goalListHead; } }
      public ClauseNode NextClause { get { return nextClause; } set { nextClause = value; } }
      public bool IsActive { get { return active; } }

      public ChoicePoint (TermNode goal, ClauseNode nextClause)
      {
        this.goalListHead = goal;
        this.nextClause = nextClause;
        active = true;
      }

      public void Kill ()
      {
        active = false;
      }

      public override String ToString ()
      {
        return String.Format ("choicepoint\r\ngoal {0}\r\nclause {1}\r\nactive {2}", goalListHead, nextClause, active);
      }
    }
    #endregion ChoicePoint

    #region CacheCheckPoint
    // pushed on the VarStack for determining whether or not to store an answer in the cache
    class CacheCheckPoint : TermNode
    {
      CachePort port;
      TermNode saveGoal;

      public CachePort Port { get { return port; } }
      public TermNode SaveGoal { get { return saveGoal; } }

      public CacheCheckPoint (CachePort port, TermNode saveGoal)
        : base ()
      {
        this.port = port;
        this.saveGoal = saveGoal;
        level = saveGoal.Level;
      }


      public override string ToString ()
      {
        return "[" + port + "-cachecheckpoint] " + saveGoal.Term.ToString () + " ...";
      }
    }
    #endregion CacheCheckPoint

    #region VarStack
    public class VarStack : Stack
    {
      Stack swap;

      public VarStack ()
        : base ()
      {
        swap = new Stack ();
      }

      //public override void Push (object o)
      //{
      //  base.Push (o);

      //  if (o is Variable)
      //    IO.WriteLine ("Pushed var functor={0} value={1}", ((Variable)o).Functor, o);
      //  else
      //    IO.WriteLine ("Pushed {0}", o);
      //}

      //public override object Pop ()
      //{
      //  object o = base.Pop ();

      //  IO.WriteLine ("Popped {0}", o);

      //  return o;
      //}

      public void DisableChoices (int n)
      {
        int i = Count;

        foreach (object o in this) // works its way down from the top !!!
        {
          if (i-- == n) return;

          if (o is SpyPoint)
            ((SpyPoint)o).Kill (); // do not retain (failure) spypoints
          else if (o is ChoicePoint)
            ((ChoicePoint)o).Kill ();
        }
      }
    }
    #endregion VarStack

    #region VarValues
    public interface IVarValue
    {
      string Name { get; }
      ITermNode Value { get; }
      string DataType { get; }
    }

    public class VarValue : IVarValue
    {
      public string name;
      public BaseTerm value;
      public string Name { get { return name; } }
      public ITermNode Value { get { return value; } }
      public string DataType { get { return value.TermType.ToString ().ToLower (); } }
      bool isSingleton;
      public bool IsSingleton { get { return isSingleton; } set { isSingleton = value; } }


      public VarValue (string name, BaseTerm value)
      {
        this.name = name;
        this.value = value;
        isSingleton = true;
      }

      public override string ToString ()
      {
        if (!value.IsVar)
        {
          bool mustPack = (value.Precedence >= 700);

          return String.Format ("{0} = {1}", name, value.ToString ().Packed (mustPack));
        }

        return null;
      }
    }

    public class VarValues : Dictionary<string, VarValue>
    {
      public BaseTerm GetValue (string name)
      {
        VarValue result;
        TryGetValue (name, out result); // result is null if value not found

        return (result == null) ? null : result.value;
      }

    }
    #endregion VarValues

    #region Solution
    public interface ISolution
    {
      IEnumerable<IVarValue> VarValuesIterator { get; }
      bool IsLast { get; }
      bool Solved { get; }
    }

    // contains the answer to a query or the response to a history command
    public class Solution : ISolution
    {
      VarValues variables;
      PrologEngine engine;
      bool solved;
      bool isLast;
      public bool Solved { get { return solved; } set { solved = value; } }
      public bool IsLast { get { return isLast; } set { isLast = value; } }
      string msg;
      IEnumerable<IVarValue> varValuesIterator;
      public IEnumerable<IVarValue> VarValuesIterator { get { return varValuesIterator; } }

      public Solution (PrologEngine engine)
      {
        this.engine = engine;
        variables = new VarValues ();
        varValuesIterator = GetEnumerator ();
        solved = true;
        msg = null;
      }


      public IEnumerable<IVarValue> GetEnumerator ()
      {
        foreach (IVarValue varValue in variables.Values)
        {
          if (engine.halted) yield break;

          yield return (varValue);
        }
      }


      public void Clear ()
      {
        variables.Clear ();
      }


      public void SetMessage (string msg)
      {
        this.msg = msg;
      }


      public void SetMessage (string msg, params object [] args)
      {
        SetMessage (string.Format (msg, args));
      }


      public void ResetMessage ()
      {
        this.msg = null;
      }


      public void SetVar (string name, BaseTerm value)
      {
        variables [name] = new VarValue (name, value);
      }


      public void RegisterVarNonSingleton (string name)
      {
        variables [name].IsSingleton = false;
      }


      public void ReportSingletons (ClauseNode c, int lineNo, ref bool firstReport)
      {
        List<string> singletons = new List<string> ();

        foreach (VarValue var in variables.Values)
          if (var.IsSingleton) singletons.Add (var.Name);

        if (singletons.Count == 0) return;

        if (firstReport)
        {
          IO.WriteLine ();
          firstReport = false;
        }

        IO.Write ("    Warning: '{0}' at line {1} has {2}singleton variable{3} [",
          c.Head.Name, lineNo,
          singletons.Count == 1 ? "a " : null,
          singletons.Count == 1 ? null : "s");

        bool first = true;

        foreach (string name in singletons)
        {
          if (first) first = false; else IO.Write (", ");

          IO.Write (name);
        }

        IO.WriteLine ("]");
      }


      public BaseTerm GetVar (string name)
      {
        return variables.GetValue (name);
      }


      public override string ToString ()
      {
        if (engine.halted) return null;

        if (msg != null) return msg;

        double totSecs = engine.ProcessorTime ().TotalSeconds;

        string time = (totSecs > 0.2)  // arbitrary threshold, show 'interesting' values only
          ? string.Format (" ({0:f3} sec)", totSecs)
          : null;

        if (!solved) return NO + time;

        StringBuilder sb = new StringBuilder ();
        string answer = null;
        BaseTerm term;

        foreach (VarValue varValue in variables.Values)
          if (!(term = varValue.value).IsVar && varValue.name [0] != '_')
            sb.AppendFormat ("\r\n {0}", varValue.ToString ());

        return answer = (sb.Length == 0 ? YES : sb.ToString ()) + time;
      }
    }
    #endregion Solution

    static readonly string WELCOME =
@"|
| Copyright (C) 2007-2014 John Pool
|
| C#Prolog comes with ABSOLUTELY NO WARRANTY. This is free software, licenced
| under the GNU General Public License, and you are welcome to redistribute it
| under certain conditions. Enter 'license.' at the command prompt for details.";
    static string VERSION = "4.1.0";
    static string RELEASE = PrologParser.VersionTimeStamp;
    static public string IntroText;

    /* The parser's terminalTable is associated with the engine, not with the parser.
     * For each consult (or read), a new instance of the parser is created. However,
     * when each parser would have a new terminalTable, any operator definitions from
     * previously consulted files would get lost (operators are symbols that must be
     * recognized by the parser and hence they are stored in the terminalTable)
     */
    BaseParser<OpDescrTriplet>.BaseTrie terminalTable;
    string query;
    Solution solution;
    static OperatorDescr CommaOpDescr;
    static OpDescrTriplet CommaOpTriplet;
    static OperatorDescr SemiOpDescr;
    static OperatorDescr EqualOpDescr;
    static OperatorDescr ColonOpDescr;
    bool halted;
    PredicateCallOptions predicateCallOptions;
#if !NETSTANDARD
    DbCommandSet dbCommandSet;
#endif
    OpenFiles openFiles;
    const int INF = Int32.MaxValue;
    VarStack varStack; // stack of variable bindings and choice points
    OperatorTable opTable;
    BracketTable wrapTable;
    BracketTable altListTable;
    GlobalTermsTable globalTermsTable;
    PredicateTable predTable;
    Stack<int> catchIdStack;
    int tryCatchId;

    public class OpenFiles : Dictionary<string, FileTerm>
    {
      public FileTerm GetFileReader (string fileName)
      {
        FileTerm ft;

        TryGetValue (fileName.ToLower (), out ft);

        return ft;
      }

      public FileTerm GetFileWriter (string fileName)
      {
        FileTerm ft;

        TryGetValue (fileName.ToLower (), out ft);

        return ft;
      }

      public void CloseAllOpenFiles ()
      {
        foreach (FileTerm ft in Values)
          ft.Close ();

        Clear ();
      }
    }

    class GlobalTermsTable
    {
      Dictionary<string, int> counterTable;
      Dictionary<string, BaseTerm> globvarTable;

      public GlobalTermsTable ()
      {
        counterTable = new Dictionary<string, int> ();
        globvarTable = new Dictionary<string, BaseTerm> ();
      }

      public void getctr (string a, out int value)
      {
        if (!counterTable.TryGetValue (a, out value))
          IO.Error ("Value of counter '{0}' is not set", a);
      }

      public void setctr (string a, int value)
      {
        counterTable [a] = value;
      }

      public void incctr (string a)
      {
        int value;

        if (counterTable.TryGetValue (a, out value))
          counterTable [a] = value + 1;
        else
          IO.Error ("Value of counter '{0}' is not set", a);
      }

      public void decctr (string a)
      {
        int value;

        if (counterTable.TryGetValue (a, out value))
          counterTable [a] = value - 1;
        else
          IO.Error ("Value of counter '{0}' is not set", a);
      }

      public void getvar (string name, out BaseTerm value)
      {
        if (!globvarTable.TryGetValue (name, out value))
          IO.Error ("Value of '{0}' is not set", name);
      }

      public void setvar (string name, BaseTerm value)
      {
        globvarTable [name] = value;
      }

      public bool varhasvalue (string name)
      {
        BaseTerm value;

        return globvarTable.TryGetValue (name, out value);
      }
    }

    TermNode goalListHead;
    BasicIo io;
    bool userInterrupted;
    bool error;
    bool trace;
    bool debug;
    bool redo; // set by CanBacktrack if a choice point was found
    bool qskip;
    bool rushToEnd;
    bool xmlTrace;
    bool reporting;  // debug (also set by 'trace') || xmlTrace
    bool profiling;
#if !NETSTANDARD
    XmlTextWriter xtw;
    string xmlFile;
    int xmlElCount; // current approximate Number of elements in the XML trace file
    int xmlMaxEl;   // maximum allowed value of xmlElCount
    bool firstGoal; // set in ExecuteGoalList() to be able to check whether a goal in the command is the very first
    bool goalListProcessed;
    bool goalListResult;
    ManualResetEvent sema;
#endif
    ClauseNode retractClause;
    int levelMin; // lowest recursion level while spying -- for determining left margin
    int levelMax; // used while spying for determining end of skip
    int prevLevel;
    ChoicePoint currentCp;
    object lastCp;
    long startTime;
    Stopwatch procTime;
    CommandHistory cmdBuf;
    static int maxWriteDepth; // Set by maxwritedepth/1. Subterms beyond this depth are written as "..."
    int queryTimeout = 0; // maximum Number of milliseconds that a command may run -- 0 means unlimited
    bool findFirstClause; // find the first clause of predicate that matches the current goal goal (-last head)
    bool csharpStrings = ConfigSettings.CSharpStrings;
    bool userSetShowStackTrace = ConfigSettings.OnErrorShowStackTrace; // default value
    bool showSingletonWarnings = true; // want to be able to turn off singleton warnings at the file level (default to true and will need to add to reset code to make sure it isn't persisting across files)

    #region unique number generators
    static int unifyCount; // total number of unifications - for tabling ('cost calculation') only
    static void NextUnifyCount () { unifyCount++; }
    static int CurrUnifyCount { get { return unifyCount; } }
    static int UnifyDelta (int refUnifyCount) { return unifyCount - refUnifyCount; }
    #endregion

    public bool Error { get { return error; } }
    public OperatorTable OpTable { get { return opTable; } }
    public BracketTable WrapTable { get { return wrapTable; } }
    public BracketTable AltListTable { get { return altListTable; } }
    public PredicateTable Ps { get { return predTable; } }
    public string Query { get { return query; } set { query = value.Trim (); } }


    static PrologEngine ()
    {
      IntroText = string.Format (
        "|\r\n| Welcome to C#Prolog MS-Windows version {0}, parser {1}\r\n{2}",
        VERSION, RELEASE, WELCOME);
      unifyCount = 0; // running total number of unifications
    }


    public PrologEngine ()
      : this (new DosIO ())
    {
    }

    public PrologEngine(BasicIo io) 
      : this(io, persistentCommandHistory: true)
    {
    }

    public PrologEngine(bool persistentCommandHistory)
      : this(new DosIO(), persistentCommandHistory)
    {
    }

    public PrologEngine(BasicIo io, bool persistentCommandHistory)
    {
        IO.BasicIO = this.io = io;
        Reset();
        cmdBuf = new CommandHistory(persistentCommandHistory);
        PostBootstrap();
    }

    public int CmdNo { get { return cmdBuf.cmdNo; } }
    public bool Debugging { get { return debug; } }
    public bool Halted { get { return halted; } set { halted = value; } }

    PrologParser parser = null;
    static string YES = "\r\n" + ConfigSettings.AnswerTrue;
    static string NO = "\r\n" + ConfigSettings.AnswerFalse;
    int gensymInt;

    static public bool MaxWriteDepthExceeded (int level)
    {
      return (maxWriteDepth != -1 && level > maxWriteDepth);
    }


    #region Engine initialization and finalization
    public void Reset ()
    {
      Initialize ();
      ReadBuiltinPredicates ();
    }


    void Initialize () // also called by ClearAll command
    {
      varStack = new VarStack ();
      solution = new Solution (this);
      SolutionIterator = GetEnumerator ();
      opTable = new OperatorTable ();
      wrapTable = new BracketTable ();
      altListTable = new BracketTable ();
      globalTermsTable = new GlobalTermsTable ();
      predTable = new PredicateTable (this);
      catchIdStack = new Stack<int> ();
      openFiles = new OpenFiles ();
      tryCatchId = 0;

      error = false;
      halted = false;
      trace = false;
      qskip = false;
      xmlTrace = false;
      startTime = -1;
      procTime = Stopwatch.StartNew();
      currentFileReader = null;
      currentFileWriter = null;
      maxWriteDepth = -1; // i.e. no max depth
      predicateCallOptions = new PredicateCallOptions ();
#if !NETSTANDARD
      xmlFile = null;
      dbCommandSet = null;
#endif
      terminalTable = new BaseParser<OpDescrTriplet>.BaseTrie (PrologParser.terminalCount, true);
      PrologParser.FillTerminalTable (terminalTable);
      parser = new PrologParser (this); // now this.terminalTable is passed on as well
    }

    void PostBootstrap ()
    {
      if (!opTable.IsBinaryOperator (PrologParser.EQ, out EqualOpDescr))
        IO.Error ("No definition found for binary operator '{0}'", PrologParser.EQ);
      else if (!opTable.IsBinaryOperator (PrologParser.COLON, out ColonOpDescr))
        IO.Error ("No definition found for binary operator '{0}'", PrologParser.COLON);
    }


    void ReadBuiltinPredicates ()
    {
      predTable.Reset ();
      parser.SetDollarAsPossibleUnquotedAtomChar (true);
      parser.StreamIn = Bootstrap.PredefinedPredicates;
      parser.SetDollarAsPossibleUnquotedAtomChar (false);
      predTable.Predefineds ["0!"] = true;
      predTable.Predefineds ["0true"] = true;
      predTable.Predefineds ["0fail"] = true;
      predTable.Predefineds ["2;"] = true;
      predTable.Predefineds ["2,"] = true;
      retractClause = predTable [BaseTerm.MakeKey ("retract", 1)].ClauseList;
      predTable.ResolveIndices ();
    }
    #endregion Engine initialization and finalization

    #region Query execution preparation and finalization
    public IEnumerable<ISolution> SolutionIterator;

    public IEnumerable<ISolution> GetEnumerator ()
    {
      try
      {
        if (PrepareSolutions (query))
          do
          {
            Execute (); // run the query

            solution.IsLast = halted || !FindChoicePoint ();

            yield return (ISolution)solution;
          }
          while (!halted && CanBacktrack (false));
        else // history command
        {
          solution.IsLast = true;

          yield return (ISolution)solution;
        }
      }
      finally
      {
        PostQueryTidyUp (); // close all potentially open files and SQL connections
      }
    }


    public ISolution GetFirstSolution (string query)
    {
      Query = query;
      IEnumerator<ISolution> solutions = SolutionIterator.GetEnumerator ();
      solutions.MoveNext ();

      return solutions.Current;
    }


    public bool PrepareSolutions (string query)
    {
      try
      {
        solution.ResetMessage ();
        solution.Solved = true;
        varStack.Clear ();
        catchIdStack.Clear ();
        predTable.Uncacheall ();
        tryCatchId = 0;
        findFirstClause = true;

        if (cmdBuf.CheckForHistoryCommands (ref query, this))
          return false;

        userInterrupted = false;
        parser.StreamIn = query;
        rushToEnd = false;
        xmlTrace = false;
        levelMin = 0;
        levelMax = INF;
        prevLevel = -1;
        lastCp = null;
        gensymInt = 0;
        io.Reset (); // clear input character buffer
        goalListHead = parser.QueryNode;
#if !NETSTANDARD
        xmlFile = null;
        xmlMaxEl = INF;
        firstGoal = true;
#endif
        if (goalListHead == null) return false;

      }
      catch (UserException x)
      {
        error = true;
        solution.SetMessage (x.Message);
        solution.Solved = false;
      }
      catch (Exception x)
      {
        error = true;
        solution.SetMessage ("{0}{1}\r\n",
          x.Message, userSetShowStackTrace ? Environment.NewLine + x.StackTrace : "");

        return false;
      }

      return true;
    }


    public void PostQueryTidyUp ()
    {
      openFiles.CloseAllOpenFiles ();
#if !NETSTANDARD
      XmlTraceClose ();
#endif
      currentFileReader = null;
      currentFileWriter = null;

#if !NETSTANDARD
      if (dbCommandSet != null)
        dbCommandSet.CloseAllConnections ();
#endif
    }
    #endregion Query execution preparation and finalization

    #region Goal last execution
    void Execute ()
    {
      ElapsedTime ();
      ProcessorTime ();

      try
      {
#if NETSTANDARD
        solution.Solved = ExecuteGoalList ();
#else
        solution.Solved = (queryTimeout == 0) ? ExecuteGoalList () : StartExecuteGoalListThread ();
#endif
      }
      catch (AbortQueryException x)
      {
        solution.SetMessage (x.Message);
        solution.Solved = true;
      }
      catch (UserException x)
      {
        error = true;
        solution.SetMessage (x.Message);
        solution.Solved = false;
      }
      catch (Exception x)
      {
        error = true;
        solution.SetMessage ("{0}{1}\r\n",
          x.Message, (userSetShowStackTrace ? Environment.NewLine + x.StackTrace : ""));
        solution.Solved = false;
      }
    }

#if !NETSTANDARD
    bool StartExecuteGoalListThread ()
    {
      ThreadStart startExecuteGoalList = new ThreadStart (RunExecuteGoalList);
      Thread run = new Thread (startExecuteGoalList);
      run.SetApartmentState (ApartmentState.MTA);
      run.Name = "ExecuteGoalList";
      run.IsBackground = true;
      sema = new ManualResetEvent (false);
      goalListProcessed = false;
      goalListResult = false;
      run.Start (); // run will fall through to WaitOne
      sema.WaitOne (queryTimeout, false); // wait for timeOutMSecs (while the RunExecuteGoalList thread runs)

      if (!goalListProcessed) // goalListProcessed is set by RunExecuteGoalList()
      {
        run.Abort ();
        solution.Solved = false;

        return IO.Error ("Query execution timed out after {0} milliseconds", queryTimeout);
      }

      return goalListResult;
    }


    void RunExecuteGoalList ()
    {
      try
      {
        goalListResult = ExecuteGoalList ();
        goalListProcessed = true;
      }
      catch (ThreadAbortException) // time-out
      {
        return;
      }
      catch (Exception e) // any other exception
      {
        error = true;
        goalListProcessed = true;
        solution.Solved = false;

        throw (e);
      }
      finally
      {
        sema.Set ();
      }
    }
#endif

    /*  Although in the code below a number of references is made to 'caching' (storing
        intermediate results of a calculation) this feature is currently not available.
        The reason is that it proved much more complicated than initially thought.
        I left the various fragments in the code, as I want to sort this out later more
        thoroughly.

        So technically spoken, the bool 'caching' will never have a true-value in the code
        below (nor anywhere else).
    */
    // The ExecuteGoalList() algorithm is the standard algorithm as for example
    // described in Ivan Bratko's "Prolog Programming for Artificial Intelligence",
    // 3rd edition p.45+
    bool ExecuteGoalList ()
    {
      // variables declaration used in goal loop to save stack space
      int stackSize;
      TermNode currClause;
      BaseTerm cleanClauseHead;
      BI builtinId;
      int level;
      BaseTerm t;
      TermNode saveGoal;
      TermNode p;
      TermNode pHead;
      TermNode pTail;
      TermNode tn0;
      TermNode tn1;
      bool caching = false;
      redo = false; // set by CanBacktrack if a choice point was found

      while (goalListHead != null) // consume the last of goalNodes until it is exhausted
      {
        if (userInterrupted)
          throw new AbortQueryException ();

        if (goalListHead.Term is TryCatchTerm)
        {
          if (goalListHead.Term is TryOpenTerm)
          {
            catchIdStack.Push (((TryOpenTerm)goalListHead.Term).Id); // CATCH-id of corresponding CATCH-clause(s) now on top
            goalListHead = goalListHead.NextGoal;

            continue;
          }
          else if (goalListHead.Term is CatchOpenTerm)
          {
            if (((CatchOpenTerm)goalListHead.Term).SeqNo == 0) // only once:
              catchIdStack.Pop (); // CATCH-id of CATCH-clause enclosing this TRY/CATCH now on top

            while (goalListHead.Term != TC_CLOSE)
              goalListHead = goalListHead.NextGoal;

            continue;
          }
          else if (goalListHead.Term == TC_CLOSE)
          {
            goalListHead = goalListHead.NextGoal;

            continue;
          }
        }
        else if (goalListHead is CacheCheckPoint) // check whether there was a successful exit of a cacheable predicate ...
        {
          CacheCheckPoint ccp = (CacheCheckPoint)goalListHead;
          saveGoal = ccp.SaveGoal;
          saveGoal.PredDescr.Cache (saveGoal.Term.Copy (), true);
          goalListHead = saveGoal.NextGoal;

          continue;
        }

        if (goalListHead is SpyPoint)
        {
          TermNode sp = ((SpyPoint)goalListHead).SaveGoal;

          // debugger resets saveGoal and returns true if user enters r(etry) or f(ail)
          if (!Debugger (SpyPort.Exit, sp, null, false, 1))
            goalListHead = sp.NextGoal;

          continue;
        }

        stackSize = varStack.Count; // varStack reflects the current program state

        // FindPredicateDefinition tries to find in the program the predicate definition for the
        // functor+arity of goalNode.Term. This definition is stored in goalListHead.NextClause
        if (findFirstClause)
        {
          if (!goalListHead.FindPredicateDefinition (predTable)) // undefined predicate
          {
            BaseTerm goal = goalListHead.Head;

            switch (predTable.ActionWhenUndefined (goal.FunctorToString, goal.Arity))
            {
              case UndefAction.Fail: // pretend the predicate exists, with 'fail' as first and only clause
                goalListHead.PredDescr = predTable [BaseTerm.FAIL.Key];
                goalListHead.NextClause = goalListHead.PredDescr.ClauseList;
                break;
              case UndefAction.Error:
                return IO.Error ("Undefined predicate: {0}", goal.Name);
              default:
                PredicateDescr pd = predTable.FindClosestMatch (goal.Name);
                string suggestion = (pd == null)
                ? null
                : string.Format (". Maybe '{0}' is what you mean?", pd.Name);
                IO.Error ("Undefined predicate: {0}{1}", goal.Name, suggestion);
                break;
            }
          }

          findFirstClause = false; // i.e. advance to the next clause upon backtracking (redoing)
        }

        if (profiling && goalListHead.PredDescr != null)
        {
          goalListHead.PredDescr.IncProfileCount ();
          caching = goalListHead.PredDescr.IsCacheable;
        }

        currClause = goalListHead.NextClause; // the first or next clause of the predicate definition

        // in order to be able to retry goal etc.
        if (reporting) varStack.Push (new SpyPoint (SpyPort.Call, goalListHead));

        saveGoal = goalListHead; // remember the original saveGoal (which may be NextGoal-ed, see below)

        if (currClause.NextClause == null) // no redo possible => fail, make explicit when tracing
        {
          if (reporting) // to be able to detect failure (i.e. when we have to pop beyond this entry) in CanBacktrack
            varStack.Push (new SpyPoint (SpyPort.Fail, goalListHead));

          if (caching)
            varStack.Push (new CacheCheckPoint (CachePort.Fail, goalListHead));
        }
        else  // currClause.NextClause will be tried upon backtracking
          varStack.Push (currentCp = new ChoicePoint (goalListHead, currClause.NextClause));

        cleanClauseHead = currClause.Head.Copy (); // instantiations must be retained for clause body -> create newVars
        level = goalListHead.Level;

        if (reporting &&
            Debugger (redo ?
            SpyPort.Redo :
            SpyPort.Call, saveGoal, cleanClauseHead, currClause.NextGoal == null, 2))
          continue;  // Debugger may return some previous version of saveGoal (retry- or fail-command)

        // UNIFICATION of the current goal and the (clause of the) predicate that matches it
        if (cleanClauseHead.Unify (goalListHead.Term, varStack))
        {
          bool currCachedClauseMustFail =
            (currClause is CachedClauseNode && !((CachedClauseNode)currClause).Succeeds);

          currClause = currClause.NextNode; // body - if any - of the matching predicate definition clause

          // FACT
          if (currClause == null) // body is null, so matching was against a fact
          {
            if (reporting && Debugger (SpyPort.Exit, goalListHead, null, false, 3)) continue;

            if (currCachedClauseMustFail) // act as if the clause has a (!, fail) body
              InsertCutFail ();
            else
              goalListHead = goalListHead.NextGoal;

            findFirstClause = true;
          }
          // BUILT-IN
          else if ((builtinId = currClause.BuiltinId) != BI.none)
          {
            if (builtinId == BI.call)
            {
              t = goalListHead.Head.Arg (0);

              if (t.IsVar) return IO.Error ("Unbound variable '{0}' in goal list", ((Variable)t).Name);

              if (goalListHead.Head.Arity > 1) // implementation of SWI call/1..8
              {
                AddCallArgs (goalListHead);
                t = goalListHead.Head;
              }

              tn0 = t.ToGoalList (stackSize, goalListHead.Level + 1);

              if (reporting)
                tn0.Append (new SpyPoint (SpyPort.Exit, saveGoal));

              goalListHead = (goalListHead == null) ? tn0 : tn0.Append (goalListHead.NextGoal);
              findFirstClause = true;
            }
            else if (builtinId == BI.or)
            {
              if (reporting)
              {
                varStack.Pop ();
                varStack.Pop ();
              }

              tn1 = goalListHead.Head.Arg (1).ToGoalList (stackSize, goalListHead.Level);
              varStack.Push (new ChoicePoint ((goalListHead == null)
                ? tn1
                : tn1.Append (goalListHead.NextGoal), null));

              tn0 = goalListHead.Head.Arg (0).ToGoalList (stackSize, goalListHead.Level);
              goalListHead = (goalListHead == null) ? tn0 : tn0.Append (goalListHead.NextGoal);
              findFirstClause = true;
            }
            else if (builtinId == BI.cut)
            {
              varStack.DisableChoices (goalListHead.Term.TermId);
              goalListHead = goalListHead.NextGoal;
              findFirstClause = true;
            }
            else if (builtinId == BI.fail)
            {
              if (!(redo = CanBacktrack ())) return false;
            }
            else if (DoBuiltin (builtinId, out findFirstClause))
            {
              if (reporting && Debugger (SpyPort.Exit, saveGoal, null, false, 5))
              {
                findFirstClause = true;

                continue;
              }
            }
            else if (!(redo = CanBacktrack ()))
              return false;
          }
          // PREDICATE RULE
          else // replace goal by body of matching clause of defining predicate
          {
            pHead = null;
            pTail = null;
            BaseTerm currTerm;

            while (currClause != null)
            {
              currTerm = currClause.Term;

              if (currTerm is TryOpenTerm)
              {
                ((TryOpenTerm)currTerm).Id = ++tryCatchId;
                p = new TermNode (currTerm, null, 0);
              }
              else if (currTerm is CatchOpenTerm)
              {
                ((CatchOpenTerm)currTerm).Id = tryCatchId; // same id as for corresponding TRY
                p = new TermNode (currTerm.Copy (false), null, 0);
              }
              else if (currTerm is Cut)
                p = new TermNode (new Cut (stackSize), null, goalListHead.Level + 1); // save the pre-unification state
              else // Copy (false): keep the varNo constant over all terms of the predicate head+body
                // (otherwise each term would get new variables, independent of their previous incarnations)
                p = new TermNode (currTerm.Copy (false), currClause.PredDescr, goalListHead.Level + 1); // gets the newVar version

              if (pHead == null)
                pHead = p;
              else
                pTail.NextGoal = p;

              pTail = p;
              currClause = currClause.NextNode;
            }

            // If caching is on for this predicate, insert a CacheCheck Exitpoint, which will be
            // picked up at the start of the loop loop if and when saveGoal has succeeded.
            // It will only be picked up if the goal has actually succeeded, because a failure would
            // have changed the goal last (and effectively removed the exit point)
            if (caching)
            {
              pTail.NextGoal = new CacheCheckPoint (CachePort.Exit, saveGoal);
              pTail = pTail.NextNode;
            }

            if (reporting)
            {
              pTail.NextGoal = new SpyPoint (SpyPort.Exit, saveGoal);
              pTail = pTail.NextNode;
            }

            pTail.NextGoal = goalListHead.NextGoal;
            goalListHead = pHead; // will never be a spypoint
            findFirstClause = true;
          }
        }
        else if (!(redo = CanBacktrack ())) // unify failed - try backtracking
          return false;
#if !NETSTANDARD
        firstGoal = false;
#endif
      } // end of while

      return true;
    }


    void InsertCutFail ()
    {
      ClauseNode fail = new ClauseNode (BaseTerm.FAIL, null);
      fail.NextGoal = goalListHead.NextGoal;
      ClauseNode cut = new ClauseNode (BaseTerm.CUT, null);
      cut.NextGoal = fail;
      goalListHead = cut;
    }


    bool CanBacktrack () // returns true if choice point was found
    {
      return CanBacktrack (true);
    }

    bool CanBacktrack (bool local) // local = false if user wants more (so as not to trigger the debugger)
    {
      Object o;
      ChoicePoint cp;

      findFirstClause = false; // to prevent resetting to the first clause upon re-entering ExecuteGoalList

      while (varStack.Count != 0)
      {
        o = varStack.Pop ();
        lastCp = o;

        if (o is CacheCheckPoint) // goal failed (otherwise we would not find this entry) -- cache a failure
        {
          TermNode saveGoal = ((CacheCheckPoint)o).SaveGoal;
          PredicateDescr pd = saveGoal.PredDescr;
          pd.Cache (saveGoal.Term.Copy (), false);
        }
        else
          if (reporting && o is SpyPoint)
          {
            if (local && ((SpyPoint)o).Port == SpyPort.Fail)
              Debugger (SpyPort.Fail, ((SpyPoint)o).SaveGoal, null, false, 6); // may reset saveGoal
          }
          else if (o is Variable)
            ((Variable)o).Unbind ();
          else if (o is ChoicePoint && ((ChoicePoint)o).IsActive)
          {
            goalListHead = (cp = (ChoicePoint)o).GoalListHead; // this was the goal we wanted to prove ...

            if (cp.NextClause == null) // no next predicate clause ...
              findFirstClause = true;  // ... so find predicate belonging to the goal last head
            else
              goalListHead.NextClause = cp.NextClause; // ... and this is next predicate clause to be tried

            return true;
          }
      }

      return false;
    }


    bool FindChoicePoint ()
    {
      foreach (Object o in varStack)
        if (o is ChoicePoint && ((ChoicePoint)o).IsActive)
          return true;

      return false;
    }
    #endregion Goal last execution

    #region TRY/CATCH
    void Throw (string exceptionClass, string exceptionMessage)
    {
      if (!SearchMatchingCatchClause (exceptionClass, exceptionMessage))
      {
        string comma = (exceptionClass == null || exceptionMessage == null) ? null : ", ";
        string msg = string.Format ("No CATCH found for throw( {0}{1}\"{2}\")",
                                    exceptionClass, comma, exceptionMessage);
        IO.Error (msg);
      }
    }

    void Throw (string exceptionClass, string exceptionFmtMessage, params object [] args)
    {
      Throw (exceptionClass, string.Format (exceptionFmtMessage, args));
    }


    void AddCallArgs (TermNode GoalListHead)
    {
      BaseTerm callPred = GoalListHead.Head.Arg (0);
      int arity0 = callPred.Arity;
      int arity = arity0 + GoalListHead.Head.Arity - 1;
      BaseTerm [] callArgs = new BaseTerm [arity];

      for (int i = 0; i < arity0; i++)
        callArgs [i] = callPred.Arg (i);

      for (int i = arity0; i < arity; i++)
        callArgs [i] = GoalListHead.Head.Arg (1 + i - arity0);

      GoalListHead.Head = new CompoundTerm (callPred.Functor, callArgs);
    }


    enum Status { NextCatchId, CompareIds, NextGoalNode, TestGoalNode, TryMatch }

    bool SearchMatchingCatchClause (string exceptionClass, string exceptionMessage)
    {
      /* A TRY/CATCH predicate has the following format:

         TRY (<terms>) CATCH [<exception class>] (<terms>) CATCH [<exception class>] (<terms>) ...

         So, a TRY/CATCH statement can have more than one CATCH-clauses, each labeled with
         the name of an 'exception class' that can be given freely by the user and that corresponds
         to the exception class name in the throw/2/3 predicate.

         When a clause containing a TRY/CATCH is expanded (i.e. when ExecuteGoalList () places
         the clause body at the beginning of the goal list), not only the TRY-body, but also the
         CATCH-bodies are put on the list. Each TRY/CATCH statement is given a unique number (Id).

         Upon normal execution (no exception occurring), TRY-bodies when appearing on the goal list
         are executed normally, whereas CATCH-bodies are simply skipped. The only action taken
         is that upon hitting upon a TRY, the Id of the TRY/CATCH is pushed on a stack,
         which indicates that in case of an exception, the CATCH-clause(s) with this Id is the
         first candidate for execution.

         When an exception occurs, the goal list is searched for the first CATCH-clause with the
         Id found on top of the stack. If the exception matches the CATCH clause (i.e. the exception
         class name supplied as throw/2/3-parameter matches the exception class name of the CATCH-
         clause OR the CATCH-clause does not have an exception class name), execution is resumed with
         the first CATCH-body predicate as head of the goal list. If none of the CATCH-clauses match,
         the next Id (beloning to the caller TRY/CATCH) is popped off the stack and the search is
         continued.

         I am not sure what to do about unbinding. It seems obvious, when control is given to a
         calling predicate, to undo the variable bindings that occurred within the called predicate.
         But what to do in the case of backtracking, when the called predicate contained a choice
         point? In that case, the variable bindings up to the choice point should be restored again,
         which seems to entail a complicated strategy of temporarily unbinding variables.
         For the time being I decided not to do any unbinding when an exception is thrown, but
         this may prove to be a wrong decision. Anyway, CatchOpenTerm has a property SaveStackSize,
         which in future may be used when the varstack has to be popped and unbindings have to
         be carried out.
      */

      if (catchIdStack.Count == 0) return false;

      int catchId = catchIdStack.Pop ();
      bool catchIdFoundInGoalList = false; // true iff the catchId was found in the list of goals
      Status status = Status.TestGoalNode;
      CatchOpenTerm t = null;

      while (true) // finite state engine implementation turned out to be easiest
      {
        switch (status)
        {
          case Status.TestGoalNode:
            if (goalListHead == null) return false;

            if (goalListHead.Term is CatchOpenTerm)
            {
              t = (CatchOpenTerm)goalListHead.Term;
              status = Status.CompareIds;
            }
            else
              status = Status.NextGoalNode;
            break;
          case Status.NextGoalNode:
            goalListHead = goalListHead.NextGoal;
            status = Status.TestGoalNode;
            break;
          case Status.NextCatchId: // get the Id of a potentially matching CATCH-clause
            if (catchIdStack.Count == 0) return false;

            catchId = catchIdStack.Pop ();
            catchIdFoundInGoalList = false;
            status = Status.CompareIds;
            break;
          case Status.CompareIds:
            if (t.Id == catchId)
            {
              catchIdFoundInGoalList = true;
              status = Status.TryMatch;
            }
            else if (catchIdFoundInGoalList) // CATCH-id does not match anymore, so try ...
              status = Status.NextCatchId;   // ... the next CATCH (at an enclosing level)
            else
              status = Status.NextGoalNode;  // catchId not yet found, try next goal
            break;
          case Status.TryMatch:
            if (t.ExceptionClass == null || t.ExceptionClass == exceptionClass)
            {
              t.MsgVar.Unify (new StringTerm (exceptionMessage), varStack);

              return true;
            }
            status = Status.NextGoalNode;
            break;
        }
      }
    }
    #endregion TRY/CATCH

    #region Debugging
    bool Debugger (SpyPort port, TermNode goalNode, BaseTerm currClause, bool isFact, int callNo)
    {
      if (!reporting) return false;

      // only called if reporting = true. This means that at least one of the following conditions hold:
      // (1) debug is true. This means that trace = true and/or we must check whether this port has a spypoint
      // (2) xmlTrace = true.
      // Console-interaction will only occur if debug && (trace || spied)
      bool spied = false;
      bool console;
      string s;
      int free = 0;
      string lmar;

      if (!trace) // determine spied-status
      {
        if (goalNode.PredDescr == null) goalNode.FindPredicateDefinition (predTable);

        spied = (goalNode.Spied && (goalNode.SpyPort | port) == goalNode.SpyPort);
      }

      console = debug && (trace || spied);

      // continue only if either trace or spied, or if an XML-trace is to be constructed
      if (!console && !xmlTrace) return false;

      lmar = null;
      BaseTerm goal = goalNode.Head;
      int level = goalNode.Level;

      if (@"\tdebug\tnodebug\tspy\tnospy\tnospyall\tconsult\ttrace\tnotrace\txmltrace\t".
        IndexOf (goal.FunctorToString) != -1) return false;

      if (console) // this part is not required for xmlTrace
      {
        if (!qskip && level >= levelMax) return false;

        levelMax = INF;   // recover from (q)s(kip) command
        qskip = false; // ...
        const int widthMin = 20; // minimal width of writeable portion of line
#if mswindows
        int width = Utils.NumCols - 10;
#else
        int width = 140;
#endif
        int indent = 3 * (level - levelMin);
        int condensedLevel = 0;

        while (indent > width - widthMin)
        {
          indent -= width - widthMin;
          condensedLevel++;
        }

        if (condensedLevel == 0)
        {
          lmar = "|  ".Repeat (level).Substring (0, indent);
          free = width - indent;
        }
        else
        {
          string dots = "| ... ";
          lmar = dots + "|  ".Repeat (level).Substring (0, indent);
          free = width - indent - dots.Length;
        }

        IO.Write (lmar);
      }

      switch (port)
      {
        case SpyPort.Call:
          if (console)
          {
            s = Utils.WrapWithMargin (goal.ToString (), lmar + "|     ", free);
            IO.Write ("{0,2:d2} Goal: {1}", level, s);
            s = Utils.WrapWithMargin (currClause.ToString (), lmar, free);
            IO.Write ("{0}{1,2:d2} {2}: {3}", lmar, level, "Try ", s);
          }
#if !NETSTANDARD
          if (xmlTrace)
          {
            if (level > prevLevel)
            {
              xtw.WriteStartElement ("body");
              xtw.WriteAttributeString ("goal", goal.ToString ());
              xtw.WriteAttributeString ("level", level.ToString ());
            }
            else if (level < prevLevel)
              xtw.WriteEndElement ();
            else
              XmlTraceWriteTerm ("goal", "goal", goal);
            XmlTraceWriteTerm ("try", isFact ? "fact" : "pred", currClause);
          }
#endif
          break;
        case SpyPort.Redo:
          if (console)
          {
            s = Utils.WrapWithMargin (currClause.ToString (), lmar + "|     ", free);
            IO.Write ("{0,2:d2} {1}: {2}", level, "Try ", s); // fact or clause
          }
#if !NETSTANDARD
          if (xmlTrace)
          {
            if (level < prevLevel) xtw.WriteEndElement ();
            XmlTraceWriteTerm ("try", isFact ? "fact" : "clause", currClause);
          }
#endif
          break;
        case SpyPort.Fail:
          if (console)
          {
            s = Utils.WrapWithMargin (goal.ToString (), lmar + "|     ", free);
            IO.Write ("{0,2:d2} Fail: {1}", level, s);
          }
#if !NETSTANDARD
          if (xmlTrace)
          {
            if (level < prevLevel) xtw.WriteEndElement ();
            XmlTraceWriteTerm ("fail", "goal", goal);
          }
#endif
          break;
        case SpyPort.Exit:
          if (console)
          {
            s = Utils.WrapWithMargin (goal.ToString (), lmar + "         ", free);
            IO.Write ("{0,2:d2} Exit: {1}", level, s);
          }
#if !NETSTANDARD
          if (xmlTrace)
          {
            if (level < prevLevel) xtw.WriteEndElement ();
            XmlTraceWriteTerm ("exit", "match", goal);
          }
#endif
          break;
      }

      prevLevel = level;

      redo = false;

      if (rushToEnd || !console) return false;

      return DoDebuggingAction (port, lmar, goalNode);
    }


    bool DoDebuggingAction (SpyPort port, string lmar, TermNode goalNode)
    {
      const string prompt = "|  TODO: ";
      const string filler = "|        ";
      int level;
      string cmd;
      int n = INF;
      int leap = 0; // difference between current level and new level

      while (true)
      {
        level = goalNode.Level;

        while (true)
        {
          IO.Write (lmar + prompt);
          cmd = IO.ReadLine ().Replace (" ", "");

          if (cmd.Length > 1)
          {
            string cmd0 = cmd.Substring (0, 1);

            try
            {
              n = Int32.Parse (cmd.Substring (1));
            }
            catch
            {
              break;
            }

            if ("sr".IndexOf (cmd0) != -1 && Math.Abs (n) > level)
              IO.WriteLine (lmar + filler + "*** Illegal value {0} -- must be in the range -{1}..{1}", n, level);
            else
            {
              if (n != INF && "cloqfgan+-.?h".IndexOf (cmd0) != -1)
                IO.WriteLine (lmar + filler + "*** Unexpected argument {0}", n);
              else
              {
                if (n < 0) { level += n; leap = -n; } else { leap = level - n; level = n; }

                cmd = cmd0;

                break;
              }
            }
          }
          else
          {
            leap = 0;

            if (cmd != "") cmd = cmd.Substring (0, 1);

            break;
          }
        }

        switch (cmd)
        {
          case "":   // creap
          case "c":  // ...
            return false;
          case "l":  // leap
            SetSwitch ("Tracing", ref trace, false);
            return false;
          case "value":  // skip
            if (n == INF) levelMax = level; else levelMax = n + 1;
            return false;
          case "t":  // out (skip to Exit or Fail)
            IO.WriteLine (lmar + filler + "*** NOT YET IMPLEMENTED");
            break;
          case "q":  // q-skip (skip subgoals except if a spypoint was set)
            levelMax = level;
            qskip = true;
            return false;
          case "r":  // retry
            if (port == SpyPort.Call && leap == 0)
            {
              IO.WriteLine (lmar + filler + "*** retry command has no effect here");

              return false;
            }
            else
            {
              RetryCurrentGoal (level);
#if !NETSTANDARD
              if (xmlTrace)
              {
                XmlTraceWriteElement ("RETRY",
                  (n == INF) ? "Retry entered by user" : String.Format ("Retry to level {0} entered by user", level));
                XmlTraceWriteEnds (leap);
              }
#endif
              return true;
            }
          case "f":  // Fail
            if (port == SpyPort.Fail)
            {
              IO.WriteLine (lmar + filler + "*** fail command has no effect here");

              return false;
            }
            else
            {
              if (!CanBacktrack ()) throw new AbortQueryException ();
#if !NETSTANDARD
              if (xmlTrace)
              {
                XmlTraceWriteElement ("FAILED",
                  (n == INF) ? "Goal failed by user" : String.Format ("Retry to level {0} entered by user", level));
                XmlTraceWriteEnds (leap);
              }
#endif
              return true;
            }
          case "i":  // ancestors
            ShowAncestorGoals (lmar + filler);
            break;
          case "n":  // nodebug
            SetSwitch ("Debugging", ref debug, false);
            return false;
          case "a":
#if !NETSTANDARD
            if (xmlTrace) XmlTraceWriteElement("ABORT", "Session aborted by user");
#endif
            throw new AbortQueryException ();
          case "+":  // spy this
            goalNode.PredDescr.SetSpy (true, goalNode.Term.FunctorToString, goalNode.Term.Arity, SpyPort.Full, false);
            return false;
          case "-":  // nospy this
            goalNode.PredDescr.SetSpy (false, goalNode.Term.FunctorToString, goalNode.Term.Arity, SpyPort.Full, false);
            return false;
          case ".":  // run to completion
            rushToEnd = true;
            return false;
          case "?":  // help
          case "h":  // ...
            string [] help = new string [] {
              "c, CR       creep       Single-step to the next port",
              "l           leap        Resume running, switch tracing off; stop at the next spypoint.",
              "value [<N>] skip        If integer N provided: skip to Exit or Fail port of level N.",
              "t           out         NOT YET IMPLEMENTED. Skip to the Exit or Fail port of the ancestor.",
              "q           quasi-skip  Same as skip, but will stop if an intermediate spypoint is found.",
              "r [<N>]     retry       Transfer control back to the Call port at level N.",
              "f           fail        Fail the current goal.",
              "i           ancestors   Show ancestor goals.",
              "n           nodebug     Switch the debugger off.",
              "a           abort       Abort the execution of the current query.",
              "+           spy this    Set a spypoint on the current goal.",
              "-           nospy this  Remove the spypoint for the current goal, if it exists.",
              ".           rush        Run to completion without furder prompting.",
              "?, h        help        Show this text."
            };
            foreach (string line in help)
              IO.WriteLine (lmar + filler + line);
            break;
          default:
            IO.WriteLine (lmar + filler + "*** Unknown command '{0}' -- enter ? or h for help", cmd);
            break;
        }
      }
    }


    public void RetryCurrentGoal (int level)
    {
      Object o;

      while (varStack.Count != 0)
      {
        o = varStack.Pop ();

        if (o is SpyPoint && ((SpyPoint)o).Port == SpyPort.Call)
        {
          goalListHead = ((SpyPoint)o).SaveGoal;

          if (goalListHead.Level == level)
          {
            goalListHead.FindPredicateDefinition (predTable); // clause had been forwarded -- reset it

            return;
          }
        }
        else if (o is Variable)
          ((Variable)o).Unbind ();
      }
    }


    void ShowAncestorGoals (string lmar)
    {
      Stack<TermNode> ancestors = new Stack<TermNode> ();
      TermNode g;
      int l;
      int lPrev = INF;

      foreach (object o in varStack) // works from the top down to the bottom
      {
        if (o is SpyPoint && ((SpyPoint)o).Port == SpyPort.Call)
        {
          if ((l = (g = ((SpyPoint)o).SaveGoal).Level) < lPrev) // level decreases or stays equal at each step
          {
            ancestors.Push (g);
            lPrev = l;
          }
        }
      }

      while (ancestors.Count != 0) // revert the order
      {
        g = ancestors.Pop ();
        IO.WriteLine (lmar + "{0}>{1}", Spaces (g.Level), g.Term);
      }
    }

#if !NETSTANDARD // Disable XML Trace feature.

    void XmlTraceOpen (string tag, int maxEl)
    {
      xmlMaxEl = maxEl;
      xmlElCount = 0;
      xmlTrace = true;
      reporting = true;
      xtw = new XmlTextWriter (xmlFile, null);
      xtw.Formatting = Formatting.Indented;
      xtw.WriteStartDocument ();
      xtw.WriteStartElement (tag);
    }


    void XmlTraceWriteTerm (string tag, string attr, BaseTerm term)
    {
      xtw.WriteStartElement (tag);
      if (term != null) xtw.WriteAttributeString (attr, term.ToString ());
      xtw.WriteEndElement ();
      XmlTraceCheckMaxElement ();
    }


    void XmlTraceWriteElement (string tag, string content)
    {
      xtw.WriteStartElement (tag);
      xtw.WriteString (content);
      xtw.WriteEndElement ();
      XmlTraceCheckMaxElement ();
    }


    void XmlTraceCheckMaxElement ()
    {
      if (xmlElCount++ < xmlMaxEl) return;

      xtw.WriteStartElement ("MAX_EXCEEDED");
      xtw.WriteString (String.Format ("Maximum number of elements ({0}) written", xmlMaxEl));
      xtw.WriteEndElement ();

      XmlTraceClose ();
    }


    void XmlTraceWriteEnds (int leap)
    {
      for (int i = 0; i < leap; i++) xtw.WriteEndElement ();
    }


    void XmlTraceClose ()
    {
      if (!xmlTrace) return;

      xtw.WriteEndElement ();
      xtw.WriteEndDocument ();
      xtw.Flush ();
      xtw.Close ();
      IO.Message ("XML trace file {0} created", xmlFile);
      xmlFile = null;
      xmlTrace = false;
    }
#endif
    #endregion Debugging

    #region Command history
    static string HistoryHelpText =
@"
  Command history commands:
  ========================
  !!                : show numbered list of previous commands
  !                 : repeat previous command
  !<n>              : repeat command number <n>
  !/<old>/<new>/    : repeat previous command, with <old> replaced by <new>.
                     / may be any char, and the end / may be omitted.
  !<n>/<old>/<new>/ : same for command number <n>
  !c                : clear the history
  !?                : help (this text)

  History commands must not be followed by a '.'
";
#if !NETSTANDARD
    [Serializable] // in order to be able to the retain history between sessions
#endif
    class CommandHistory : List<string>
    {
#if !NETSTANDARD
      ApplicationStorage persistentSettings;
#endif
      public int cmdNo { get { return Count + 1; } }
      int maxNo; // maximum number of commands to be retained

      public CommandHistory()
#if NETSTANDARD
          : this(enablePersistence: false)
#else
          : this(enablePersistence: true)
#endif
      {
      }

      public CommandHistory (bool enablePersistence)
      {
          maxNo = Math.Abs (ConfigSettings.HistorySize);
#if NETSTANDARD
          if (enablePersistence)
          {
            //TODO: enable persistence
            //throw new NotImplementedException();
            persistentSettings = new ApplicationStorage();
              List<string> history;

              try
              {
                  history = persistentSettings.Get<List<string>>("CommandHistory", null);
              }
              catch
              {
                  history = null;
              }
          
              if (history == null) return;
          
              foreach (string cmd in history) Add(cmd);
          }
#else
          if (enablePersistence)
          {
              persistentSettings = new ApplicationStorage();
              List<string> history;

              try
              {
                  history = persistentSettings.Get<List<string>>("CommandHistory", null);
              }
              catch
              {
                  history = null;
              }
          
              if (history == null) return;
          
              foreach (string cmd in history) Add(cmd);
          }
#endif
      }


      new void Add (string cmd)
      {
        if (Count == 0 || cmd != this [Count - 1]) base.Add (cmd);
      }


      // returns true if a history command was entered
      public bool CheckForHistoryCommands (ref string query, PrologEngine engine)
      {
        string s = query.Trim ();
        int i;

        if (s.Length == 0) return true;

        if (s.EndsWith ("."))
        {
          Add (query);

          return false;
        }
        else if (s [0] == '!')
        {
          int len = s.Length;

          if (s == "!!")
          {
            engine.solution.SetMessage (HistoryList);
            query = null;

            return true;
          }
          else if (s == "!c")
          {
            ClearHistory ();
            engine.solution.SetMessage ("\r\n--- Command history cleared");
            query = null;

            return true;
          }
          else if (s == "!?")
          {
            engine.solution.SetMessage (HistoryHelpText);
            query = null;

            return true;
          }

          if (len == 1)
          {
            if (Count > 0) Add (query = this [Count - 1].ToString ());
          }
          else if (int.TryParse (s.Substring (1, len - 1), out i))
          {
            if (i < 1 || i > Count)
            {
              query = null;

              return true;
            }

            Add (query = this [i - 1].ToString ());
          }
          else
          {
            // check for find/replace: ![commandno]<sepchar><findstr><sepchar><replacestr>[<sepchar>]
            Regex r = new Regex (@"^!(?<cno>\d+)?(?<sep>\S).{3,}$"); // find the command nr, the separator char, and check on length
            Match m = r.Match (query);

            if (!m.Success)
            {
              IO.Error ("Unrecognized history command '{0}'", s);
              query = null;

              return true;
            }

            char sep = m.Groups ["sep"].Value [0];
            int cmdNo = (m.Groups ["cno"].Captures.Count > 0) ?
              Convert.ToInt32 (m.Groups ["cno"].Value) : Count;

            if (cmdNo < 1 || cmdNo > Count)
            {
              IO.Error ("No command {0}", cmdNo);
              query = null;

              return true;
            }

            // replace oldSep in command by newSep some char that is bound not to occur in command

            r = new Regex (@"^!\d*(?:\f(?<str>[^\f]*)){2}\f?$"); // 2 occurances of str
            m = r.Match (query.Replace (sep, '\f'));

            CaptureCollection cc;

            if (!m.Success || (cc = m.Groups ["str"].Captures) [0].Value.Length == 0)
            {
              IO.Error ("Unrecognized history command '{0}'", s);
              query = null;

              return true;
            }

            Add (query = this [cmdNo - 1].ToString ().Replace (cc [0].Value, cc [1].Value));
          }

          IO.WriteLine ("?- " + query);

          return false;
        }
        else if (query.Trim ().EndsWith ("/")) // TEMP
        {
          query = "/";
          engine.halted = true;

          return true;
        }
        else
          return false;
      }


      string HistoryList
      {
        get
        {
          StringBuilder sb = new StringBuilder ();

          for (int i = 0; i < Count; i++) sb.AppendFormat ("\r\n{0,2} {1}", i + 1, this [i]);

          return sb.ToString ();
        }
      }


      void ClearHistory ()
      {
        Clear ();
#if !NETSTANDARD
        if (persistentSettings != null) persistentSettings["CommandHistory"] = null;
#endif
      }


      public void Persist ()
      {
#if !NETSTANDARD
        int maxNum = Math.Min (Count, maxNo);
        if (persistentSettings != null) persistentSettings["CommandHistory"] = GetRange(Count - maxNum, maxNum);
#endif
      }
    }


    public void PersistCommandHistory ()
    {
      try
      {
        if (ConfigSettings.HistorySize > 0)
          cmdBuf.Persist ();
      }
      catch (Exception x)
      {
        IO.Error ("Unable to save command history. System message is:\r\n{0}", x.Message);
      }
    }
    #endregion Command history

    #region Named variables
    public BaseTerm GetVariable (string s)
    {
      return solution.GetVar (s);
    }


    public void SetVariable (BaseTerm t, string s)
    {
      solution.SetVar (s, t);
    }


    public void EraseVariables ()
    {
      solution.Clear ();
    }

    public void RegisterVarNonSingleton (string s)
    {
      solution.RegisterVarNonSingleton (s);
    }

    public void ReportSingletons (ClauseNode c, int lineNo, ref bool firstReport)
    {  
      solution.ReportSingletons (c, lineNo, ref firstReport);
    }
    #endregion Named variables

    #region Miscellaneous
    public void SetProfiling (bool mode)
    {
      profiling = mode;
    }


    public void Console_CancelKeyPress (object sender, ConsoleCancelEventArgs e)
    {
      userInterrupted = true;
      e.Cancel = true;
    }


    public string Prompt
    {
      get
      {
        return string.Format ("\r\n{0}{1} ?- ", (Debugging ? "[d]" : ""), CmdNo);
      }
    }


    public int ElapsedTime () // returns numer of milliseconds since last Call
    {
      long prevStartTime = (startTime == -1) ? DateTime.Now.Ticks : startTime;

      return (int)((startTime = DateTime.Now.Ticks) - prevStartTime) / 10000;
    }


    public TimeSpan ProcessorTime () // returns numer of milliseconds since last Call
    {
      var processorTime = procTime.Elapsed;
      procTime.Reset();
      procTime.Start();
      return processorTime;
    }


    public long ClockTicksMSecs ()
    {
      return DateTime.Now.Ticks / 10000;
    }


    public void CheckInitialConsultFile ()
    {
      string initialConsultFileName = PrologEngine.ConfigSettings.InitialConsultFile;

      if (string.IsNullOrEmpty (initialConsultFileName)) return;

      if (File.Exists (initialConsultFileName))
      {
        IO.WriteLine ();
        Consult (initialConsultFileName);
      }
      else
      {
        string msg = string.Format (
          "Initial file to be consulted not found (config file says: '{0}')\r\n",
            initialConsultFileName);

        IO.Warning (msg);
      }
    }


    public void CheckConfigFile ()
    {
#if !NETSTANDARD
      string configFileName = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;

      if (!File.Exists (configFileName))
      {
        string msg = string.Format (
          "No config file ({0}) found: default settings used", configFileName);

        IO.Warning (msg);
      }
#endif
    }


    public void Consult (string fileName)
    {
      bool csharpStringsSave = csharpStrings;
      // string as ISO-style charcode lists or as C# strings

      try
      {
        predTable.Consult (fileName);
      }
      finally
      {
        csharpStrings = csharpStringsSave;
      }
    }

    public void Consult (Stream stream, string streamName = null)
    {
      bool csharpStringsSave = csharpStrings;
      // string as ISO-style charcode lists or as C# strings

      try
      {
        predTable.Consult (stream, streamName);
      }
      finally
      {
        csharpStrings = csharpStringsSave;
      }
    }

    public void ConsultFromString (string prologCode, string codeTitle = null)
    {
      using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(prologCode)))
        Consult(ms, codeTitle);
    }

    public void CreateFact (string functor, BaseTerm [] args)
    {
      predTable.Assert (new CompoundTerm (functor, args), true);
    }


    public void SetStringStyle (BaseTerm t)
    {
      string arg = t.FunctorToString;

      if (!(arg == "csharp" || arg == "iso"))
        IO.Error ("Illegal argument '{0}' for setstringstyle/1 -- must be 'iso' or 'csharp'", t);

      csharpStrings = (arg == "csharp");
    }


    void SetSwitch (string switchName, ref bool switchVar, bool mode)
    {
      bool current = switchVar;

      if (current == (switchVar = mode))
        IO.Message ("{0} already {1}", switchName, (mode ? "on" : "off"));
      else
        IO.Message ("{0} switched {1}", switchName, (mode ? "on" : "off"));
    }
    #endregion Miscellaneous

  }
  #endregion Engine
}
