//#define arg1index // if (un)defined, do the same in TermNodeList.cs !!!
#define enableSpying
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
using System.Resources;
using System.Reflection;
using System.Collections.Specialized;
using System.Text;
using System.IO;

namespace Prolog
{
#if NETSTANDARD
  using Hashtable = System.Collections.Generic.Dictionary<object, object>;
  using SortedList = System.Collections.Generic.SortedList<object, object>;
  internal static class HashtableExtension
  {
    public static bool Contains(this Hashtable value, object key) => value.ContainsKey(key);
  }

#endif

  public partial class PrologEngine
  {
    public enum UndefAction { None, Fail, Succeed, Warning, Error }

    public class PredicateTable
    {
      PrologEngine engine;
      Dictionary<string, PredicateDescr> predTable;
      Hashtable predefineds;
      Hashtable moduleName;
      Hashtable definedInCurrFile;
      Hashtable isDiscontiguous;
      Dictionary<string, UndefAction> actionWhenUndefined; // currently not used
      CrossRefTable crossRefTable;
      bool crossRefInvalid;
      string prevIndex = null;
      bool allDiscontiguous = false;
      // True if a predicate's clauses are not grouped together but are scattered
      // over a source file. Should normally not be used (used for code from others).
      const string SLASH = "/";
      public Hashtable Predefineds { get { return predefineds; } }
      Stack<string> consultFileStack;
      Stack<PrologParser> consultParserStack;
      string ConsultFileName { get { return (consultFileStack.Count == 0) ? null : consultFileStack.Peek (); } }

      public PredicateTable (PrologEngine engine)
      {
        this.engine = engine;
        predTable = new Dictionary<string, PredicateDescr> ();
        crossRefTable = new CrossRefTable ();
        crossRefInvalid = true;
        predefineds = new Hashtable ();
        moduleName = new Hashtable ();
        definedInCurrFile = new Hashtable ();
        isDiscontiguous = new Hashtable ();
        actionWhenUndefined = new Dictionary<string, UndefAction> ();
        consultFileStack = new Stack<string> ();
        consultParserStack = new Stack<PrologParser> ();
      }


      public void Reset ()
      {
        predTable.Clear ();
        predefineds.Clear ();
        moduleName.Clear ();
        definedInCurrFile.Clear ();
        isDiscontiguous.Clear ();
        actionWhenUndefined.Clear ();
        prevIndex = null;
        consultFileStack.Clear ();
        consultParserStack.Clear ();
      }


      public PredicateDescr this [string key]
      {
        get
        {
          PredicateDescr result;
          predTable.TryGetValue (key, out result);

          return result;
        }
        set
        {
          predTable [key] = value;
        }
      }


      public bool IsPredefined (string key)
      {
        return predefineds.Contains (key);
      }


      public void InvalidateCrossRef ()
      {
        crossRefInvalid = true;
      }


      public void SetActionWhenUndefined (string f, int a, UndefAction u)
      {
        actionWhenUndefined [BaseTerm.MakeKey (f, a)] = u;
      }


      public UndefAction ActionWhenUndefined (string f, int a)
      {
        UndefAction u;
        actionWhenUndefined.TryGetValue (BaseTerm.MakeKey (f, a), out u);

        return u;
      }


      public bool IsPredicate (string functor, int arity)
      {
        return this.Contains (BaseTerm.MakeKey (functor, arity));
      }


#if enableSpying
      public bool SetSpy (bool enabled, string functor, int arity, BaseTerm list)
      {
        SpyPort ports;

        if (list == null)
          ports = SpyPort.Full;
        else
        {
          ports = SpyPort.None;
          string s;

          while (list.Arity == 2)
          {
            s = list.Arg (0).FunctorToString;

            try
            {
              ports |= (SpyPort)Enum.Parse (typeof (SpyPort), s);
            }
            catch
            {
              IO.Error ("Illegal value '{0}'", s);
            }
            list = list.Arg (1);
          }
        }

        PredicateDescr pd;

        if (arity == -1)
        {
          bool found = false;

          foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
            if ((pd = kv.Value).Functor == functor)
            {
              found = true;
              pd.SetSpy (enabled, pd.Functor, pd.Arity, ports, !enabled);
            }

          if (!found) IO.Error ("Predicate does not exist: {0}", functor);

          return found;
        }
        else
        {
          predTable.TryGetValue (BaseTerm.MakeKey (functor, arity), out pd);

          if (pd == null)
          {
            IO.Error ("Predicate does not exist: {0}/{1}", functor, arity);

            return false;
          }

          pd.SetSpy (enabled, functor, arity, ports, !enabled);
        }

        return true;
      }

      public void SetNoSpyAll ()
      {
        PredicateDescr pd;

        foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
          ((pd = kv.Value)).SetSpy (false, pd.Functor, pd.Arity, SpyPort.None, false);
      }

      public void ShowSpypoints ()
      {
        foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
          (kv.Value).ShowSpypoint ();
      }
#endif

      PredicateDescr SetClauseList (string f, int a, ClauseNode c)
      {
        string key = BaseTerm.MakeKey (f, a);
        PredicateDescr pd = this [key];

        if (pd == null)
        {
          this [key] = pd =
         new PredicateDescr (null, ConsultFileName, f, a, c);
        }
        else
          pd.SetClauseListHead (c);

        pd.AdjustClauseListEnd ();

        return pd;
      }


      public bool Contains (string key)
      {
        return (this [key] != null);
      }


      public override string ToString ()
      {
        return predTable.ToString ();
      }

      public int Consult(string fileName)
      {
        return Consult(null, fileName);
      }

      public int Consult (Stream stream, string streamName = null)
      {
        // string as ISO-style charcode lists or as C# strings
        var fileName = streamName ?? Guid.NewGuid().ToString("N");
        consultFileStack.Push (fileName);
        consultParserStack.Push (Globals.CurrentParser);
        PrologParser parser = Globals.CurrentParser = new PrologParser (engine);
        allDiscontiguous = false;

       try
       {
          prevIndex = null;
          definedInCurrFile.Clear ();
          isDiscontiguous.Clear ();
          Uncacheall ();
          //Globals.ConsultModuleName = null;
          parser.Prefix = "&program\r\n";
          IO.Write ("--- Consulting {0} ... ", fileName);
          parser.LoadFromStream (stream, fileName);
          IO.WriteLine ("{0} lines read", parser.LineCount);
          InvalidateCrossRef ();
        }
        finally
        {
        engine.showSingletonWarnings = true; // set it back to the default value of true

        Globals.CurrentParser = consultParserStack.Pop (); ;
          //Globals.ConsultModuleName = null; // Currently not used
        }

        return parser.LineCount;
      }


      public void AddPredefined (ClauseNode clause)
      {
        BaseTerm head = clause.Head;
        string key = head.Key;
        PredicateDescr pd = this [key];

        if (pd == null)
        {
          predefineds [key] = true; // any value != null will do
          SetClauseList (head.FunctorToString, head.Arity, clause); // create a PredicateDescr
        }
        else if (prevIndex != null && key != prevIndex)
          IO.Error ("Definition for predefined predicate '{0}' must be contiguous", head.Index);
        else
          pd.AppendToClauseList (clause);

        prevIndex = key;
      }


      public void SetDiscontiguous (BaseTerm t)
      {
        if (t == null || t.FunctorToString != SLASH || !t.Arg (0).IsAtom || !t.Arg (1).IsInteger)
          IO.Error ("Illegal or missing argument '{0}' for discontiguous/1", t);

        // The predicate descriptor does not yet exist (and may even not come at all!)
        string key = BaseTerm.MakeKey (t.Arg (0).FunctorToString, t.Arg (1).To<short> ());

        //IO.WriteLine ("--- Setting discontiguous for {0} in definitionFile {1}", key, Globals.ConsultFileName);
        isDiscontiguous [key] = "true";
      }


      public void SetDiscontiguous (bool mode)
      {
        allDiscontiguous = mode;
      }


      public void HandleSimpleDirective (PrologParser p, string directive, string argument, int arity)
      {
        //IO.WriteLine ("HandleSimpleDirective ({0}, {1}, {2})", directive, argument, arity);

        switch (directive)
        {
          case "workingdir":
            ConfigSettings.SetWorkingDirectory (argument);
            break;
          case "fail_if_undefined":
            SetActionWhenUndefined (argument, arity, UndefAction.Fail);
            break;
          case "cache":
            SetCaching (argument, arity, true);
            break;
          case "nocache":
            SetCaching (argument, arity, false);
            break;
          case "cacheall":
            SetCaching (null, 0, true);
            break;
          case "nocacheall":
            SetCaching (null, 0, false);
            break;
          case "stacktrace":
            if (argument == "on")
              engine.userSetShowStackTrace = true;
            else if (argument == "off")
              engine.userSetShowStackTrace = false;
           else
              IO.Error (":- stacktrace: illegal argument '{0}'; use 'on' or 'off' instead", argument);
            break;
          case "style_check_singleton_warning":
            if (argument == "on")
                engine.showSingletonWarnings = true;
            else if (argument == "off")
                engine.showSingletonWarnings = false;
            else
                IO.Error(":- style_check_singleton_warning: illegal argument '{0}'; use 'on' or 'off' instead. It is 'on' by default.", argument);
            break;
                    case "initialization":
            IO.Warning ("':- initialization' directive not implemented -- ignored");
            break;
          default:
            IO.Error ("Unknown directive ':- {0}'", directive);
            break;
        }
      }


      public void SetModuleName (string n)
      {
        object o = moduleName [n];
        string currFile = ConsultFileName;

        if (o == null)
        {
          moduleName [n] = currFile;
          // ConsultModuleName = null;
        }
        else if ((string)o != currFile)
          IO.Error ("Module name {0} already declared in file {1}", n, (string)o);

        // ACTUAL FUNCTIONALITY TO BE IMPLEMENTED, using a 
        // ConsultModuleName stack, analoguous to ConsultFileName
      }


      public void AddClause (ClauseNode clause)
      {
        BaseTerm head = clause.Head;

        string key = head.Key;
        string index = head.Index;

        if (predefineds.Contains (key))
          IO.Error ("Modification of predefined predicate {0} not allowed", index);

        if (prevIndex == key) // previous clause was for the same predicate
        {
          PredicateDescr pd = this [key];
          pd.AppendToClauseList (clause);
        }
        else // first predicate or different predicate
        {
          PredicateDescr pd = this [key];

          if (!definedInCurrFile.Contains (key)) //  very first clause of this predicate in this file -- reset at start of consult
          {
            if (pd != null && pd.DefinitionFile != ConsultFileName)
              IO.Error ("Predicate '{0}' is already defined in {1}", index, pd.DefinitionFile);

            definedInCurrFile [key] = true;
            pd = SetClauseList (head.FunctorToString, head.Arity, clause); // implicitly erases all previous definitions
            pd.IsDiscontiguous = (isDiscontiguous.Contains (key) || allDiscontiguous);
            prevIndex = key;
          }
          else // not the first clause. First may be from another definitionFile (which is an error).
          {    // If from same, IsDiscontiguous must hold, unless DiscontiguousAllowed = "1" in .config
            bool b = false;

            if (pd.IsDiscontiguous || (b = ConfigSettings.DiscontiguousAllowed))
            {
              if (b)
                IO.Warning ("Predicate '{0}' is defined discontiguously but is not declared as such", index);

              if (pd.DefinitionFile == ConsultFileName)
                pd.AppendToClauseList (clause);
              else // OK
                IO.Error ("Discontiguous predicate {0} must be in one file (also found in {1})", index, pd.DefinitionFile);
            }
            else if (pd.DefinitionFile == ConsultFileName) // Warning or Error?
              IO.Error ("Predicate '{0}' occurs discontiguously but is not declared as such", index);
            else
              IO.Error ("Predicate '{0}' is already defined in {1}", index, pd.DefinitionFile);
          }
        }
      }


      #region assert/retract
      public void Assert (BaseTerm assertion, bool asserta)
      {
        BaseTerm head;
        TermNode body = null;
        PredicateDescr pd;
        BaseTerm assertionCopy = assertion.Copy (true);

        if (assertionCopy.HasFunctor (PrologParser.IMPLIES))
        {
          head = assertionCopy.Arg (0);
          body = assertionCopy.Arg (1).ToGoalList ();
        }
        else
          head = assertionCopy;

        if (!head.IsCallable) IO.Error ("Illegal predicate head '{0}'", head.ToString ());

        string key = head.Key;

        if ((predefineds.Contains (key)) || (head.Precedence >= 1000))
          IO.Error ("assert/1 cannot be applied to predefined predicate or operator '{0}'",
            assertionCopy.Index);

        predTable.TryGetValue (key, out pd);
        ClauseNode newC = new ClauseNode (head, body);

        if (pd == null) // first head
        {
          SetClauseList (head.FunctorToString, head.Arity, newC);
          ResolveIndices ();
        }
        else if (pd.IsCacheable)
          IO.Error ("assert/1 cannot be applied to cached predicate '{0}'",
            assertionCopy.Index);
        else if (asserta) // at beginning
        {
          newC.NextClause = pd.ClauseList; // pd.ClauseList may be null
          SetClauseList (head.FunctorToString, head.Arity, newC);
#if arg1index
          pd.CreateFirstArgIndex (); // re-create
#endif
        }
        else // at end
        {
          pd.AppendToClauseList (newC);
#if arg1index
          pd.CreateFirstArgIndex (); // re-create
#endif
        }

        InvalidateCrossRef ();
      }

      public bool Retract (BaseTerm t, VarStack varStack, BaseTerm where)
      {
        string key = t.Key;

        if (predefineds.Contains (key))
          IO.Error ("retract of predefined predicate {0} not allowed", key);

        PredicateDescr pd = this [key];

        if (pd == null) return false;

        InvalidateCrossRef ();
        ClauseNode c = pd.ClauseList;
        ClauseNode prevc = null;
        BaseTerm cleanTerm;
        int top;

        while (c != null)
        {
          cleanTerm = c.Head.Copy ();

          top = varStack.Count;

          if (cleanTerm.Unify (t, varStack)) // match found -- remove this term from the chain
          {
            if (prevc == null) // remove first clause
            {
              if (c.NextClause == null) // we are about to remove the last remaining clause for this predicate
              {
                predTable.Remove (key);        // ... so remove its PredicateDescr as well
#if arg1index
                pd.CreateFirstArgIndex (); // re-create
#endif
                ResolveIndices ();
              }
              else
                pd.SetClauseListHead (c.NextClause);
            }
            else // not the first
            {
              prevc.NextClause = c.NextClause;
              prevc = c;
              pd.AdjustClauseListEnd ();
#if arg1index
              pd.CreateFirstArgIndex (); // re-create
#endif
            }

            return true; // possible bindings must stay intact (e.g. if p(a) then retract(p(X)) yields X=a)
          }

          Variable s;
          for (int i = varStack.Count - top; i > 0; i--) // unbind all vars that got bound by the above Unification
          {
            s = (Variable)varStack.Pop ();
            s.Unbind ();
          }

          prevc = c;
          c = c.NextClause;
        }

        ResolveIndices ();

        return false;
      }


      public bool RetractAll (BaseTerm t, VarStack varStack)
      {
        // remark: first-argument indexing is not affected by deleting clauses

        string key = t.Key;

        if (predefineds.Contains (key))
          IO.Error ("retract of predefined predicate {0} not allowed", key);

        PredicateDescr pd = this [key];

        if (pd == null) return true;

        ClauseNode c = pd.ClauseList;
        ClauseNode prevc = null;
        bool match = false;

        while (c != null)
        {
          BaseTerm cleanTerm = c.Term.Copy ();

          if (cleanTerm.IsUnifiableWith (t, varStack)) // match found -- remove this head from the chain
          {
            match = true; // to indicate that at least one head was found

            if (prevc == null) // remove first clause
            {
              if (c.NextClause == null) // we are about to remove the last remaining clause for this predicate
              {
                predTable.Remove (key); // ... so remove its PredicateDescr as well

                break;
              }
              else
                pd.SetClauseListHead (c.NextClause);
            }
            else // not the first
            {
              prevc.NextClause = c.NextClause;
              prevc = c;
            }
          }
          else
            prevc = c;

          c = c.NextClause;
        }

        if (match)
        {
#if arg1index
          pd.DestroyFirstArgIndex (); // rebuilt by ResolveIndices()
#endif
          pd.AdjustClauseListEnd ();
          ResolveIndices ();
        }

        return true;
      }


      public bool Abolish (string functor, int arity)
      {
        string key = BaseTerm.MakeKey (functor, arity);

        if (predefineds.Contains (key))
          IO.Error ("abolish of predefined predicate '{0}/{1}' not allowed", functor, arity);

        PredicateDescr pd = this [key];

        if (pd == null) return false;

        predTable.Remove (key);

#if arg1index
        pd.DestroyFirstArgIndex (); // rebuilt by ResolveIndices()
#endif
        ResolveIndices ();

        return true;
      }
      #endregion assert/retract


      bool ListClause (PredicateDescr pd, string functor, int arity, int seqno)
      {
        ClauseNode clause = null;
        string details;

        if ((clause = pd.ClauseList) == null) return false;

        details = "source: " + pd.DefinitionFile;

//        if (pd.IsFirstArgIndexed) details += "; arg1-indexed (jump points marked with '.')";

        IO.WriteLine ("\r\n{0}/{1}: ({2}) {3}", functor, arity, details,
          ((seqno == 1) ? "" : (seqno.ToString ().Packed ())));

        while (clause != null)
        {
          bool currCachedClauseMustFail =
            (clause is CachedClauseNode && !((CachedClauseNode)clause).Succeeds);

          TermNode next;

//          // prefix a clause that is pointed to by first-argument indexing with '.'
//          IO.Write (" {0}{1}", (pd.IsFirstArgMarked (clause))?".":" ", nextClause.Term);
          IO.Write ("  {0}", clause.Term);

          if (currCachedClauseMustFail)
            IO.Write (" :- !, fail");
          else if ((next = clause.NextNode) != null)
          {
            BI builtinId = next.BuiltinId;
            IO.Write (" :-{0}", (builtinId == BI.none)
              ? next.ToString ()
              : Environment.NewLine + builtinId.ToString ());
          }

          IO.WriteLine (".");
          clause = clause.NextClause;
        }

        return true;
      }


      public bool ListAll (string functor, int arity, bool showPredefined, bool showUserDefined)
      {
        bool result = false; // no predicate <functor>/<arity> assumed
        PredicateDescr pd;

        // for sorting the predicates alphabetically:
        SortedDictionary<string, PredicateDescr> sl = new SortedDictionary<string, PredicateDescr> ();

        foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
        {
          pd = kv.Value;

          if (functor == null || functor == pd.Functor)
          {
            bool isPredefined = IsPredefined (kv.Key);

            if ((showPredefined && showUserDefined ||
               showPredefined && isPredefined ||
               showUserDefined && !isPredefined) &&
               (arity == -1 || arity == pd.Arity))
              sl.Add (pd.Functor + pd.Arity.ToString (), pd);
          }
        }

        int seqNo = 0;

        foreach (KeyValuePair<string, PredicateDescr> kv in sl)
          result = ListClause (pd = kv.Value, pd.Functor, pd.Arity, ++seqNo) || result;

        return result;
      }


      // CACHEING CURRENTLY NOT USED
      #region cacheing
      public bool SetCaching (string functor, int arity, bool value)
      {
        if (functor == null) // clear entire cache
        {
          Uncacheall ();

          IO.Message ("Entire cache cleared");

          return true;
        }

        PredicateDescr pd;
        bool found = false;

        foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
        {
          pd = kv.Value;

          if (functor == pd.Functor && (arity == -1 || pd.Arity == arity))
          {

            pd.IsCacheable = value;

            if (!value) pd.Uncache ();// remove cached values

            found = true;

            if (value)
              IO.Message ("Caching set on {0}/{1}", functor, pd.Arity);
            else
              IO.Message ("Caching removed from {0}/{1}", functor, pd.Arity);
          }
        }

        if (!found)
          if (arity == -1)
            IO.Error ("Predicate '{0}' not found", functor);
          else
            IO.Error ("Predicate '{0}/{1}' not found", functor, arity);

        return found;
      }

      // remove all cached clauses from all predicates
      public void Uncacheall ()
      {
        foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
          kv.Value.Uncache ();
      }
      #endregion cacheing


      #region cross reference table
      void SetupCrossRefTable () //TODO (later...): deal with arguments of not/1 and call/1
      {
        if (!crossRefInvalid) return;

        crossRefTable.Reset ();
        PredicateDescr pd;

        foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
        {
          pd = kv.Value;
          bool isPredefined = IsPredefined (kv.Key);
          ClauseNode clause = pd.ClauseList;

          if (!isPredefined) crossRefTable.AddPredicate (pd);

          // iterate over NextClause and NextClause.NextNode
          while (clause != null)
          {
            TermNode node = clause.NextNode;

            while (node != null)
            {
              if (node.PredDescr != null && !isPredefined)
              {
                PredicateDescr npd;
                //IO.WriteLine ("{0} uses {1}", pd.Name, node.PredDescr.Name);
                crossRefTable [pd, npd = node.PredDescr] = false;

                if (npd.Name == "not/1" || npd.Name == "call/1") // add args to cref
                {
                  TermNode arg = node.NextNode;
                  IO.WriteLine ("{0} arg is {1}", npd.Name, arg);
                }
              }

              node = node.NextNode;
            }

            clause = clause.NextClause;
          }
        }

        crossRefTable.CalculateClosure ();
        crossRefInvalid = false;
      }


      public void CrossRefTableToSpreadsheet (string fileName)
      {
        SetupCrossRefTable ();
        crossRefTable.GenerateCsvFile (fileName);
      }
      #endregion


      public bool ShowHelp (string functor, int arity, out string suggestion)
      {
        suggestion = null;
        string HELPRES = "CsProlog.CsPrologHelp"; // default HELPRES

        Assembly asm = Assembly.Load(new AssemblyName("CSProlog"));

        //TODO: I've hardcoded the assembly name is CSProlog in Assembly.Load rather than using the AssemblyQualifiedName (was getting an error - not sure why and don't want to fight with it)
        //Assembly asm = Assembly.Load(new AssemblyName(GetType().AssemblyQualifiedName));

        string [] res = asm.GetManifestResourceNames (); // pick the right functor from res and put in in HELPRES
        foreach (string s in res)
        {
            //IO.WriteLine("FunctorInGetManifestResourceNames: " + s); // uncomment to see the resources - maybe help identify which one it should be if having problmes
            //Could be CSProlog.CSPrologHelp.resources  OR  CSProlog.Core.CSPrologHelp.resources --- the below loop should find the right one and user it
            if (s.Contains("CsPrologHelp.resources"))
            {
              HELPRES = s.Replace(".resources", "");
              break;
            }
        }

        ResourceManager rm = new ResourceManager (HELPRES, asm);

        if (functor == null)
        {
          IO.WriteLine (rm.GetString ("help$"));
          IO.WriteLine ("\r\n  (*) contains the description of a feature rather than a predicate.");
          IO.WriteLine ("\r\n  Usage: help <predicate>[/<arity>] or help( <predicate>[/<arity>]).");
          IO.WriteLine ("\r\n  File CsPrologHelp.txt contains the help texts and a description of how to re-create help."); //TODO: I can't seem to find this file. Eliminate the line or create the file???
          IO.WriteLine("\r\n  Facts must be read from a file before they can be queried. try  help(consult). to see how to read in from a file.");

          return true;
        }
        else if (functor == "history")
        {
          IO.Write (HistoryHelpText);

          return true;
        }

        string [] arities;

        if (arity == -1) // no arity given: show all predicates for 'functor'
        {
          arities = new string [] { rm.GetString (functor) }; // returns something like "/0/2/3"

          if (arities [0] == null) return false;

          if (arities [0] == "(*)") // a little hacky
            arities [0] = "*";
          else
            arities = arities [0].Split (new char [] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        }
        else
          arities = new string [] { arity.ToString () };

        bool found = false;
        StringBuilder sb = new StringBuilder ();
        string content;

        foreach (string a in arities)
        {
          string key = functor + '/' + a;
          content = rm.GetString (key);

          if (content == null) break;

          sb.AppendLine (content.TrimEnd ());
          found = true;
        }

        const string DASHES =
          "  -------------------------------------------------------------------------------------------";


        if (found)
          IO.WriteLine ("\r\n{0}\r\n{1}\r\n{0}", DASHES, sb.ToString ());
        else
        {
          PredicateDescr pd = FindClosestMatch (functor + (arity > 0 ? '/' + arity.ToString () : null));
           suggestion =
            (pd == null) ? null : string.Format (" Maybe '{0}' is what you mean?", pd.Name);
        }

        return found;
      }


      public void ResolveIndices () // functor/arity-key resolution
      {
        PredicateDescr pd;

        foreach (KeyValuePair<string, PredicateDescr> kv in predTable) // traverse all program predicates
        {
          ResolveIndex (pd = kv.Value);
          pd.CreateFirstArgIndex (); // check whether first-argument indexing is applicable, and build the index if so
        }
      }


      void ResolveIndex (PredicateDescr pd)
      {
        ClauseNode clause = pd.ClauseList;

        while (clause != null) // iterate over all clauses of this predicate. NextClause.BaseTerm contains predicate clauseHead
        {
          BaseTerm clauseHead = clause.Head; // clause = clauseHead :- clauseTerm*
          TermNode clauseTerm = clause.NextNode;

          while (clauseTerm != null) // non-facts only. Iterate over all clauseTerm-terms at of this clause
          {
            if (clauseTerm.BuiltinId == BI.none) clauseTerm.PredDescr = this [clauseTerm.Term.Key];
            // builtins (>=0) are handled differently (in Execute ())

            clauseTerm = clauseTerm.NextNode;
          }
          clause = clause.NextClause;
        }
        return;
      }


      #region profile count -- for storing predicate call counts (profile/0/1 command)
      class ProfileCountList : List <KeyValuePair<int, string>>
      {
        class ProfileCountComparer : IComparer<KeyValuePair<int, string>>
        {
          public int Compare (KeyValuePair<int, string> kv0, KeyValuePair<int, string> kv1)
          {
            int result = -kv0.Key.CompareTo (kv1.Key); // descending count order

            if (result == 0) return kv0.Value.CompareTo (kv1.Value);

            return result;
          }
        }

        static IComparer<KeyValuePair<int, string>> SortProfileCounts
        { get { return (IComparer<KeyValuePair<int, string>>) new ProfileCountComparer (); } }

        public void Add (int count, string name)
        {
          Add (new KeyValuePair<int, string> (count, name));
        }

        public new void Sort ()
        {
          base.Sort (SortProfileCounts);
        }

        public int MaxNameLen (int maxEntry)
        {
          int maxNameLen = 0;
          int i = 0;

          foreach (KeyValuePair<int, string> kv in this)
          {
            if (i++ == maxEntry) break;

            maxNameLen = Math.Max (maxNameLen, kv.Value.Length);
          }

          return maxNameLen;
        }
     }


      public void ShowProfileCounts (int maxEntry) // maximum number of entries to be shown
      {
        ProfileCountList profile = new ProfileCountList ();
        int maxLen = 0;
        int maxVal = 0;

        foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
          if (!IsPredefined (kv.Key) && kv.Value.ProfileCount > 0)
          {
            profile.Add (kv.Value.ProfileCount, kv.Value.Name);
            maxVal = Math.Max (maxVal, kv.Value.ProfileCount);
            maxLen = 1 + (int)Math.Log10 ((double)maxVal);
          }

        profile.Sort ();

        IO.WriteLine ();
        string format =
          "  {0,-" + profile.MaxNameLen (maxEntry).ToString () +
          "} : {1," + maxLen.ToString () + ":G}";

        int entryCount = 0;

        foreach (KeyValuePair<int, string> kv in profile)
        {
          if (entryCount++ > maxEntry) break;

          IO.WriteLine (format, kv.Value, kv.Key);
        }
      }


      public void ClearProfileCounts ()
      {
        foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
          kv.Value.ProfileCount = 0;
      }
      #endregion profile counts


      public void FindUndefineds ()
      {
        SortedList sd = new SortedList ();

        foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
          FindUndefined (sd, kv.Value);

        IO.WriteLine ("The following predicates are undefined:");
#if NETSTANDARD
        foreach (var kv in sd) IO.WriteLine ("  {0}", kv.Key);
#else
        foreach (DictionaryEntry kv in sd) IO.WriteLine ("  {0}", kv.Key);
#endif
      }


      void FindUndefined (SortedList sd, PredicateDescr pd)
      {
        ClauseNode clause = pd.ClauseList;
        TermNode clauseTerm;

        while (clause != null) // iterate over all clauses of this predicate
        {
          clauseTerm = clause.NextNode;

          while (clauseTerm != null) // non-facts only. Iterate over all clauseTerm-terms of this clause
          {
            if (clauseTerm.BuiltinId == BI.none && clauseTerm.PredDescr == null)
              sd [clauseTerm.Term.Index] = null;

            clauseTerm = clauseTerm.NextNode;
          }

          clause = clause.NextClause;
        }

        return;
      }

      // try to match the name of an unrecognised command
      public PredicateDescr FindClosestMatch (string predName)
      {
        const float THRESHOLD = 0.5F; // maximum value for likely match
        PredicateDescr closestMatch = null;
        float closestMatchValue = 1.0F;

        foreach (KeyValuePair<string, PredicateDescr> kv in predTable)
        {
          PredicateDescr pd = kv.Value;
          float matchValue;

          if ((matchValue = pd.Name.Levenshtein (predName)) < closestMatchValue)
          {
            closestMatchValue = matchValue;
            closestMatch = pd;
          }
        }

        return (closestMatchValue < THRESHOLD) ? closestMatch : null;
      }
    }

    // classes for iterating over a predicate's clauses (used by clause/2)
    #region ClauseIterator
    class ClauseIterator : IEnumerable<BaseTerm>
    {
      PredicateDescr pd;
      BaseTerm clauseHead;
      IEnumerator<BaseTerm> iterator;
      BaseTerm clauseBody;
      public BaseTerm ClauseBody { get { return clauseBody; } }
      VarStack varStack;

      public ClauseIterator (PredicateTable predTable, BaseTerm clauseHead, VarStack varStack)
      {
        this.pd = predTable [clauseHead.Key]; // null if not found
        this.clauseHead = clauseHead;
        this.varStack = varStack;
        iterator = GetEnumerator ();
      }

      // A predicate consists of one or more clauses. A clause consist of a head and optionally a
      // body. A head is a term, the body is a sequence of terms. A predicate is stored as a chain
      // of TermNodes, where each TermNode represents a clause. These TermNodes are linked via the
      // nextClause field. In each nextClause/TermNode the clause head is stored in term, and the
      // clause body (which may be null) in NextNode.

      public IEnumerator<BaseTerm> GetEnumerator ()
      {
        if (pd == null) yield break;

        ClauseNode clause = pd.ClauseList;

        while (clause != null) // iterate over all clauses of this predicate
        {
          TermNode bodyNode = clause.NextNode;

          int marker = varStack.Count; // register the point to which we must undo unification

          if (clause.Head.Unify (clauseHead, varStack))
          {
            if (bodyNode == null) // a fact
              clauseBody = new BoolTerm (true);
            else if (bodyNode.BuiltinId == BI.none)
              clauseBody = bodyNode.TermSeq ();
            else
              clauseBody = new StringTerm ("<builtin>");

            yield return clauseBody;
          }

          // undo unification with clauseHead before attempting the next clause head
          BaseTerm.UnbindToMarker (varStack, marker);
          clause = clause.NextClause;
        }
      }


      IEnumerator IEnumerable.GetEnumerator ()
      {
        return GetEnumerator ();
      }


      public bool MoveNext ()
      {
        return iterator.MoveNext ();
      }
    }
    #endregion ClauseIterator
  }
}
