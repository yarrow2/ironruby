/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Microsoft Public License. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Microsoft Public License, please send an email to 
 * dlr@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Microsoft Public License.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

using IronPython.Compiler.Ast;
using IronPython.Hosting;
using IronPython.Runtime;
using IronPython.Runtime.Types;

#if CLR2
using Microsoft.Scripting.Math;
#else
using System.Numerics;
#endif

namespace IronPython.Compiler {

    public class Parser : IDisposable { // TODO: remove IDisposable
        // immutable properties:
        private readonly Tokenizer _tokenizer;

        // mutable properties:
        private ErrorSink _errors;
        private ParserSink _sink;

        // resettable properties:
        private SourceUnit _sourceUnit;

        /// <summary>
        /// Language features initialized on parser construction and possibly updated during parsing. 
        /// The code can set the language features (e.g. "from __future__ import division").
        /// </summary>
        private ModuleOptions _languageFeatures;

        // state:
        private TokenWithSpan _token;
        private TokenWithSpan _lookahead;
        private Stack<FunctionDefinition> _functions;
        private bool _fromFutureAllowed;
        private string _privatePrefix;
        private bool _parsingStarted, _allowIncomplete;
        private bool _inLoop, _inFinally, _isGenerator, _returnWithValue;
        private SourceCodeReader _sourceReader;
        private int _errorCode;
        private readonly CompilerContext _context;

        private static readonly char[] newLineChar = new char[] { '\n' };
        private static readonly char[] whiteSpace = { ' ', '\t' };
      
        #region Construction

        private Parser(CompilerContext context, Tokenizer tokenizer, ErrorSink errorSink, ParserSink parserSink, ModuleOptions languageFeatures) {
            ContractUtils.RequiresNotNull(tokenizer, "tokenizer");
            ContractUtils.RequiresNotNull(errorSink, "errorSink");
            ContractUtils.RequiresNotNull(parserSink, "parserSink");

            tokenizer.ErrorSink = new TokenizerErrorSink(this);

            _tokenizer = tokenizer;
            _errors = errorSink;
            _sink = parserSink;
            _context = context;

            Reset(tokenizer.SourceUnit, languageFeatures);
        }

        public static Parser CreateParser(CompilerContext context, PythonOptions options) {
            return CreateParserWorker(context, options, false);
        }

        [Obsolete("pass verbatim via PythonCompilerOptions in PythonOptions")]
        public static Parser CreateParser(CompilerContext context, PythonOptions options, bool verbatim) {
            return CreateParserWorker(context, options, verbatim);
        }

        private static Parser CreateParserWorker(CompilerContext context, PythonOptions options, bool verbatim) {
            ContractUtils.RequiresNotNull(context, "context");
            ContractUtils.RequiresNotNull(options, "options");

            PythonCompilerOptions compilerOptions = context.Options as PythonCompilerOptions;
            if (options == null) {
                throw new ArgumentException(Resources.PythonContextRequired);
            }

            SourceCodeReader reader;

            try {
                reader = context.SourceUnit.GetReader();

                if (compilerOptions.SkipFirstLine) {
                    reader.ReadLine();
                }
            } catch (IOException e) {
                context.Errors.Add(context.SourceUnit, e.Message, SourceSpan.Invalid, 0, Severity.Error);
                throw;
            }

            Tokenizer tokenizer = new Tokenizer(context.Errors, compilerOptions, verbatim);
            tokenizer.Initialize(null, reader, context.SourceUnit, SourceLocation.MinValue);
            tokenizer.IndentationInconsistencySeverity = options.IndentationInconsistencySeverity;

            Parser result = new Parser(context, tokenizer, context.Errors, context.ParserSink, compilerOptions.Module);
            result._sourceReader = reader;
            return result;
        }

        #endregion

        #region Public parser interface

        public PythonAst ParseFile(bool makeModule) {
            return ParseFile(makeModule, false);
        }

        //single_input: Newline | simple_stmt | compound_stmt Newline
        //eval_input: testlist Newline* ENDMARKER
        //file_input: (Newline | stmt)* ENDMARKER
        public PythonAst ParseFile(bool makeModule, bool returnValue) {
            try {
                return ParseFileWorker(makeModule, returnValue);
            } catch (BadSourceException bse) {
                throw BadSourceError(bse);
            }
        }
        
        //[stmt_list] Newline | compound_stmt Newline
        //stmt_list ::= simple_stmt (";" simple_stmt)* [";"]
        //compound_stmt: if_stmt | while_stmt | for_stmt | try_stmt | funcdef | classdef
        //Returns a simple or coumpound_stmt or null if input is incomplete
        /// <summary>
        /// Parse one or more lines of interactive input
        /// </summary>
        /// <returns>null if input is not yet valid but could be with more lines</returns>
        public PythonAst ParseInteractiveCode(out ScriptCodeParseResult properties) {
            bool parsingMultiLineCmpdStmt;
            bool isEmptyStmt = false;

            properties = ScriptCodeParseResult.Complete;

            StartParsing();
            Statement ret = InternalParseInteractiveInput(out parsingMultiLineCmpdStmt, out isEmptyStmt);

            if (_errorCode == 0) {
                if (isEmptyStmt) {
                    properties = ScriptCodeParseResult.Empty;
                } else if (parsingMultiLineCmpdStmt) {
                    properties = ScriptCodeParseResult.IncompleteStatement;
                }

                if (isEmptyStmt) {
                    return null;
                }

                return new PythonAst(ret, false, _languageFeatures, true, _context);
            } else {
                if ((_errorCode & ErrorCodes.IncompleteMask) != 0) {
                    if ((_errorCode & ErrorCodes.IncompleteToken) != 0) {
                        properties = ScriptCodeParseResult.IncompleteToken;
                        return null;
                    }

                    if ((_errorCode & ErrorCodes.IncompleteStatement) != 0) {
                        if (parsingMultiLineCmpdStmt) {
                            properties = ScriptCodeParseResult.IncompleteStatement;
                        } else {
                            properties = ScriptCodeParseResult.IncompleteToken;
                        }
                        return null;
                    }
                }

                properties = ScriptCodeParseResult.Invalid;
                return null;
            }
        }

        public PythonAst ParseSingleStatement() {
            try {
                StartParsing();

                MaybeEatNewLine();
                Statement statement = ParseStmt();
                EatEndOfInput();
                return new PythonAst(statement, false, _languageFeatures, true, _context);
            } catch (BadSourceException bse) {
                throw BadSourceError(bse);
            }
        }

        public PythonAst ParseTopExpression() {
            try {
                // TODO: move from source unit  .TrimStart(' ', '\t')
                ReturnStatement ret = new ReturnStatement(ParseTestListAsExpression());
                ret.SetLoc(SourceSpan.None);
                return new PythonAst(ret, false, _languageFeatures, false, _context);
            } catch (BadSourceException bse) {
                throw BadSourceError(bse);
            }
        }

        /// <summary>
        /// Given the interactive text input for a compound statement, calculate what the
        /// indentation level of the next line should be
        /// </summary>
        public static int GetNextAutoIndentSize(string text, int autoIndentTabWidth) {
            ContractUtils.RequiresNotNull(text, "text");

            Debug.Assert(text[text.Length - 1] == '\n');
            string[] lines = text.Split(newLineChar);
            if (lines.Length <= 1) return 0;
            string lastLine = lines[lines.Length - 2];

            // Figure out the number of white-spaces at the start of the last line
            int startingSpaces = 0;
            while (startingSpaces < lastLine.Length && lastLine[startingSpaces] == ' ')
                startingSpaces++;

            // Assume the same indent as the previous line
            int autoIndentSize = startingSpaces;
            // Increase the indent if this looks like the start of a compounds statement.
            // Ideally, we would ask the parser to tell us the exact indentation level
            if (lastLine.TrimEnd(whiteSpace).EndsWith(":"))
                autoIndentSize += autoIndentTabWidth;

            return autoIndentSize;
        }

        public ErrorSink ErrorSink {
            get {
                return _errors;
            }
            set {
                ContractUtils.RequiresNotNull(value, "value");
                _errors = value;
            }
        }

        public ParserSink ParserSink {
            get {
                return _sink;
            }
            set {
                ContractUtils.RequiresNotNull(value, "value");
                _sink = value;
            }
        }

        public int ErrorCode {
            get { return _errorCode; }
        }

        public void Reset(SourceUnit sourceUnit, ModuleOptions languageFeatures) {
            ContractUtils.RequiresNotNull(sourceUnit, "sourceUnit");

            _sourceUnit = sourceUnit;
            _languageFeatures = languageFeatures;
            _token = new TokenWithSpan();
            _lookahead = new TokenWithSpan();
            _fromFutureAllowed = true;
            _functions = null;
            _privatePrefix = null;

            _parsingStarted = false;
            _errorCode = 0;
        }

        public void Reset() {
            Reset(_sourceUnit, _languageFeatures);
        }

        #endregion

        #region Error Reporting

        private void ReportSyntaxError(TokenWithSpan t) {
            ReportSyntaxError(t, ErrorCodes.SyntaxError);
        }

        private void ReportSyntaxError(TokenWithSpan t, int errorCode) {
            ReportSyntaxError(t.Token, t.Span, errorCode, true);
        }

        private void ReportSyntaxError(Token t, SourceSpan span, int errorCode, bool allowIncomplete) {
            SourceLocation start = span.Start;
            SourceLocation end = span.End;

            if (allowIncomplete && (t.Kind == TokenKind.EndOfFile || (_tokenizer.IsEndOfFile && (t.Kind == TokenKind.Dedent || t.Kind == TokenKind.NLToken)))) {
                errorCode |= ErrorCodes.IncompleteStatement;
            }

            string msg = String.Format(System.Globalization.CultureInfo.InvariantCulture, GetErrorMessage(t, errorCode), t.Image);

            ReportSyntaxError(start, end, msg, errorCode);
        }

        private static string GetErrorMessage(Token t, int errorCode) {
            string msg;
            if ((errorCode & ~ErrorCodes.IncompleteMask) == ErrorCodes.IndentationError) {
                msg = Resources.ExpectedIndentation;
            } else if (t.Kind != TokenKind.EndOfFile) {
                msg = Resources.UnexpectedToken;
            } else {
                msg = "unexpected EOF while parsing";
            }
            
            return msg;
        }

        private void ReportSyntaxError(string message) {
            ReportSyntaxError(_lookahead.Span.Start, _lookahead.Span.End, message);
        }

        internal void ReportSyntaxError(SourceLocation start, SourceLocation end, string message) {
            ReportSyntaxError(start, end, message, ErrorCodes.SyntaxError);
        }

        internal void ReportSyntaxError(SourceLocation start, SourceLocation end, string message, int errorCode) {
            // save the first one, the next error codes may be induced errors:
            if (_errorCode == 0) {
                _errorCode = errorCode;
            }
            _errors.Add(_sourceUnit, message, new SourceSpan(start, end), errorCode, Severity.FatalError);
        }

        #endregion        

        #region LL(1) Parsing

        private static bool IsPrivateName(string name) {
            return name.StartsWith("__") && !name.EndsWith("__");
        }

        private string FixName(string name) {
            if (_privatePrefix != null && IsPrivateName(name)) {
                name = string.Format("_{0}{1}", _privatePrefix, name);
            }

            return name;
        }

        private string ReadNameMaybeNone() {
            // peek for better error recovery
            Token t = PeekToken();
            if (t == Tokens.NoneToken) {
                NextToken();
                return "None";
            }

            NameToken n = t as NameToken;
            if (n == null) {
                ReportSyntaxError("syntax error");
                return null;
            }

            NextToken();
            return FixName(n.Name);
        }

        private string ReadName() {
            NameToken n = PeekToken() as NameToken;
            if (n == null) {
                ReportSyntaxError(_lookahead);
                return null;
            }
            NextToken();
            return FixName(n.Name);
        }

        //stmt: simple_stmt | compound_stmt
        //compound_stmt: if_stmt | while_stmt | for_stmt | try_stmt | funcdef | classdef
        private Statement ParseStmt() {
            switch (PeekToken().Kind) {
                case TokenKind.KeywordIf:
                    return ParseIfStmt();
                case TokenKind.KeywordWhile:
                    return ParseWhileStmt();
                case TokenKind.KeywordFor:
                    return ParseForStmt();
                case TokenKind.KeywordTry:
                    return ParseTryStatement();
                case TokenKind.At:
                    return ParseDecorated();
                case TokenKind.KeywordDef:
                    return ParseFuncDef();
                case TokenKind.KeywordClass:
                    return ParseClassDef();
                case TokenKind.KeywordWith:
                    return ParseWithStmt();
                default:
                    return ParseSimpleStmt();
            }
        }

        //simple_stmt: small_stmt (';' small_stmt)* [';'] Newline
        private Statement ParseSimpleStmt() {
            Statement s = ParseSmallStmt();
            if (MaybeEat(TokenKind.Semicolon)) {
                SourceLocation start = s.Start;
                List<Statement> l = new List<Statement>();
                l.Add(s);
                while (true) {
                    if (MaybeEatNewLine() || MaybeEat(TokenKind.EndOfFile)) {
                        break;
                    }

                    l.Add(ParseSmallStmt());

                    if (MaybeEat(TokenKind.EndOfFile)) {
                        // implies a new line
                        break;
                    } else if (!MaybeEat(TokenKind.Semicolon)) {
                        EatNewLine();
                        break;
                    }
                }
                Statement[] stmts = l.ToArray();

                SuiteStatement ret = new SuiteStatement(stmts);
                ret.SetLoc(start, stmts[stmts.Length - 1].End);
                return ret;
            } else if (!MaybeEat(TokenKind.EndOfFile) && !EatNewLine()) {
                // error handling, make sure we're making forward progress
                NextToken();                
            }
            return s;
        }

        /*
        small_stmt: expr_stmt | print_stmt  | del_stmt | pass_stmt | flow_stmt | import_stmt | global_stmt | exec_stmt | assert_stmt

        del_stmt: 'del' exprlist
        pass_stmt: 'pass'
        flow_stmt: break_stmt | continue_stmt | return_stmt | raise_stmt | yield_stmt
        break_stmt: 'break'
        continue_stmt: 'continue'
        return_stmt: 'return' [testlist]
        yield_stmt: 'yield' testlist
        */
        private Statement ParseSmallStmt() {
            switch (PeekToken().Kind) {
                case TokenKind.KeywordPrint:
                    return ParsePrintStmt();
                case TokenKind.KeywordPass:
                    return FinishSmallStmt(new EmptyStatement());
                case TokenKind.KeywordBreak:
                    if (!_inLoop) {
                        ReportSyntaxError("'break' outside loop");
                    }
                    return FinishSmallStmt(new BreakStatement());
                case TokenKind.KeywordContinue:
                    if (!_inLoop) {
                        ReportSyntaxError("'continue' not properly in loop");
                    } else if (_inFinally) {
                        ReportSyntaxError("'continue' not supported inside 'finally' clause");
                    }
                    return FinishSmallStmt(new ContinueStatement());
                case TokenKind.KeywordReturn:
                    return ParseReturnStmt();
                case TokenKind.KeywordFrom:
                    return ParseFromImportStmt();
                case TokenKind.KeywordImport:
                    return ParseImportStmt();
                case TokenKind.KeywordGlobal:
                    return ParseGlobalStmt();
                case TokenKind.KeywordRaise:
                    return ParseRaiseStmt();
                case TokenKind.KeywordAssert:
                    return ParseAssertStmt();
                case TokenKind.KeywordExec:
                    return ParseExecStmt();
                case TokenKind.KeywordDel:
                    return ParseDelStmt();
                case TokenKind.KeywordYield:
                    return ParseYieldStmt();
                default:
                    return ParseExprStmt();
            }
        }

        // del_stmt: "del" target_list
        //  for error reporting reasons we allow any expression and then report the bad
        //  delete node when it fails.  This is the reason we don't call ParseTargetList.
        private Statement ParseDelStmt() {
            NextToken();
            SourceLocation start = GetStart();
            List<Expression> l = ParseExprList();
            foreach (Expression e in l) {
                string delError = e.CheckDelete();
                if (delError != null) {
                    ReportSyntaxError(e.Span.Start, e.Span.End, delError, ErrorCodes.SyntaxError);
                }
            }

            DelStatement ret = new DelStatement(l.ToArray());
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        private Statement ParseReturnStmt() {
            if (CurrentFunction == null) {
                ReportSyntaxError(IronPython.Resources.MisplacedReturn);
            }
            NextToken();
            Expression expr = null;
            SourceLocation start = GetStart();
            if (!NeverTestToken(PeekToken())) {
                expr = ParseTestListAsExpr(true);
            }

            if (expr != null) {
                _returnWithValue = true;
                if (_isGenerator) {
                    ReportSyntaxError("'return' with argument inside generator");
                }
            }

            ReturnStatement ret = new ReturnStatement(expr);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        private Statement FinishSmallStmt(Statement stmt) {
            NextToken();
            stmt.SetLoc(GetStart(), GetEnd());
            return stmt;
        }


        private Statement ParseYieldStmt() {
            // For yield statements, continue to enforce that it's currently in a function. 
            // This gives us better syntax error reporting for yield-statements than for yield-expressions.
            FunctionDefinition current = CurrentFunction;
            if (current == null) {
                ReportSyntaxError(IronPython.Resources.MisplacedYield);
            }

            _isGenerator = true;
            if (_returnWithValue) {
                ReportSyntaxError("'return' with argument inside generator");
            }

            Eat(TokenKind.KeywordYield);

            // See Pep 342: a yield statement is now just an expression statement around a yield expression.
            Expression e = ParseYieldExpression();
            Debug.Assert(e != null); // caller already verified we have a yield.

            Statement s = new ExpressionStatement(e);
            s.SetLoc(e.Span);
            return s;
        }

        /// <summary>
        /// Peek if the next token is a 'yield' and parse a yield expression. Else return null.
        /// 
        /// Called w/ yield already eaten.
        /// </summary>
        /// <returns>A yield expression if present, else null. </returns>
        // yield_expression: "yield" [expression_list] 
        private Expression ParseYieldExpression() {
            // Mark that this function is actually a generator.
            // If we're in a generator expression, then we don't have a function yet.
            //    g=((yield i) for i in range(5))
            // In that acse, the genexp will mark IsGenerator. 
            FunctionDefinition current = CurrentFunction;
            if (current != null) {
                current.IsGenerator = true;
            }

            SourceLocation start = GetStart();

            // Parse expression list after yield. This can be:
            // 1) empty, in which case it becomes 'yield None'
            // 2) a single expression
            // 3) multiple expression, in which case it's wrapped in a tuple.
            Expression yieldResult;

            bool trailingComma;
            List<Expression> l = ParseExpressionList(out trailingComma);
            if (l.Count == 0) {
                // Check empty expression and convert to 'none'
                yieldResult = new ConstantExpression(null);
            } else if (l.Count != 1) {
                // make a tuple
                yieldResult = MakeTupleOrExpr(l, trailingComma);
            } else {
                // just take the single expression
                yieldResult = l[0];
            }

            Expression yieldExpression = new YieldExpression(yieldResult);

            yieldExpression.SetLoc(start, GetEnd());
            return yieldExpression;

        }

        private Statement FinishAssignments(Expression right) {
            List<Expression> left = new List<Expression>();

            while (MaybeEat(TokenKind.Assign)) {
                string assignError = right.CheckAssign();
                if (assignError != null) {
                    ReportSyntaxError(right.Span.Start, right.Span.End, assignError, ErrorCodes.SyntaxError | ErrorCodes.NoCaret);
                }

                left.Add(right);

                if (MaybeEat(TokenKind.KeywordYield)) {
                    right = ParseYieldExpression();
                } else {
                    bool trailingComma;
                    var exprs = ParseExpressionList(out trailingComma);
                    if (exprs.Count == 0) {
                        ReportSyntaxError(left[0].Start, left[0].End, "invalid syntax");
                    }
                    right = MakeTupleOrExpr(exprs, trailingComma);
                }
            }

            Debug.Assert(left.Count > 0);

            AssignmentStatement assign = new AssignmentStatement(left.ToArray(), right);
            assign.SetLoc(left[0].Start, right.End);
            return assign;
        }

        // expr_stmt: expression_list
        // expression_list: expression ( "," expression )* [","] 
        // assignment_stmt: (target_list "=")+ (expression_list | yield_expression) 
        // augmented_assignment_stmt ::= target augop (expression_list | yield_expression) 
        // augop: '+=' | '-=' | '*=' | '/=' | '%=' | '**=' | '>>=' | '<<=' | '&=' | '^=' | '|=' | '//='
        private Statement ParseExprStmt() {
            Expression ret = ParseTestListAsExpr(false);
            if (ret is ErrorExpression) {
                NextToken();
            }

            if (PeekToken(TokenKind.Assign)) {
                return FinishAssignments(ret);
            } else {
                PythonOperator op = GetAssignOperator(PeekToken());
                if (op != PythonOperator.None) {
                    NextToken();
                    Expression rhs;

                    if (MaybeEat(TokenKind.KeywordYield)) {
                        rhs = ParseYieldExpression();
                    } else {
                        rhs = ParseTestListAsExpr(false);
                    }

                    string assignError = ret.CheckAugmentedAssign();
                    if (assignError != null) {
                        ReportSyntaxError(assignError);
                    }

                    AugmentedAssignStatement aug = new AugmentedAssignStatement(op, ret, rhs);
                    aug.SetLoc(ret.Start, GetEnd());
                    return aug;
                } else {
                    Statement stmt = new ExpressionStatement(ret);
                    stmt.SetLoc(ret.Span);
                    return stmt;
                }
            }
        }

        private PythonOperator GetAssignOperator(Token t) {
            switch (t.Kind) {
                case TokenKind.AddEqual: return PythonOperator.Add;
                case TokenKind.SubtractEqual: return PythonOperator.Subtract;
                case TokenKind.MultiplyEqual: return PythonOperator.Multiply;
                case TokenKind.DivideEqual: return TrueDivision ? PythonOperator.TrueDivide : PythonOperator.Divide;
                case TokenKind.ModEqual: return PythonOperator.Mod;
                case TokenKind.BitwiseAndEqual: return PythonOperator.BitwiseAnd;
                case TokenKind.BitwiseOrEqual: return PythonOperator.BitwiseOr;
                case TokenKind.ExclusiveOrEqual: return PythonOperator.Xor;
                case TokenKind.LeftShiftEqual: return PythonOperator.LeftShift;
                case TokenKind.RightShiftEqual: return PythonOperator.RightShift;
                case TokenKind.PowerEqual: return PythonOperator.Power;
                case TokenKind.FloorDivideEqual: return PythonOperator.FloorDivide;
                default: return PythonOperator.None;
            }
        }


        private PythonOperator GetBinaryOperator(OperatorToken token) {
            switch (token.Kind) {
                case TokenKind.Add: return PythonOperator.Add;
                case TokenKind.Subtract: return PythonOperator.Subtract;
                case TokenKind.Multiply: return PythonOperator.Multiply;
                case TokenKind.Divide: return TrueDivision ? PythonOperator.TrueDivide : PythonOperator.Divide;
                case TokenKind.Mod: return PythonOperator.Mod;
                case TokenKind.BitwiseAnd: return PythonOperator.BitwiseAnd;
                case TokenKind.BitwiseOr: return PythonOperator.BitwiseOr;
                case TokenKind.ExclusiveOr: return PythonOperator.Xor;
                case TokenKind.LeftShift: return PythonOperator.LeftShift;
                case TokenKind.RightShift: return PythonOperator.RightShift;
                case TokenKind.Power: return PythonOperator.Power;
                case TokenKind.FloorDivide: return PythonOperator.FloorDivide;
                default:
                    string message = String.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        Resources.UnexpectedToken,
                        token.Kind);
                    Debug.Assert(false, message);
                    throw new ArgumentException(message);
            }
        }


        // import_stmt: 'import' module ['as' name"] (',' module ['as' name])*        
        // name: identifier
        private ImportStatement ParseImportStmt() {
            Eat(TokenKind.KeywordImport);
            SourceLocation start = GetStart();

            List<ModuleName> l = new List<ModuleName>();
            List<string> las = new List<string>();
            l.Add(ParseModuleName());
            las.Add(MaybeParseAsName());
            while (MaybeEat(TokenKind.Comma)) {
                l.Add(ParseModuleName());
                las.Add(MaybeParseAsName());
            }
            ModuleName[] names = l.ToArray();
            var asNames = las.ToArray();

            ImportStatement ret = new ImportStatement(names, asNames, AbsoluteImports);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        // module: (identifier '.')* identifier
        private ModuleName ParseModuleName() {
            SourceLocation start = GetStart();
            ModuleName ret = new ModuleName(ReadNames());
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        // relative_module: "."* module | "."+
        private ModuleName ParseRelativeModuleName() {
            SourceLocation start = GetStart();

            int dotCount = 0;
            while (MaybeEat(TokenKind.Dot)) {
                dotCount++;
            }

            string[] names = ArrayUtils.EmptyStrings;
            if (PeekToken() is NameToken) {
                names = ReadNames();
            }

            ModuleName ret;
            if (dotCount > 0) {
                ret = new RelativeModuleName(names, dotCount);
            } else {
                if (names.Length == 0) {
                    ReportSyntaxError(_lookahead.Span.Start, _lookahead.Span.End, "invalid syntax");
                }
                ret = new ModuleName(names);
            }

            ret.SetLoc(start, GetEnd());
            return ret;
        }

        private string[] ReadNames() {
            List<string> l = new List<string>();
            l.Add(ReadName());
            while (MaybeEat(TokenKind.Dot)) {
                l.Add(ReadName());
            }
            return l.ToArray();
        }


        // 'from' relative_module 'import' identifier ['as' name] (',' identifier ['as' name]) *
        // 'from' relative_module 'import' '(' identifier ['as' name] (',' identifier ['as' name])* [','] ')'        
        // 'from' module 'import' "*"                                        
        private FromImportStatement ParseFromImportStmt() {
            Eat(TokenKind.KeywordFrom);
            SourceLocation start = GetStart();
            ModuleName dname = ParseRelativeModuleName();

            Eat(TokenKind.KeywordImport);

            bool ateParen = MaybeEat(TokenKind.LeftParenthesis);

            string[] names;
            string[] asNames;
            bool fromFuture = false;

            if (MaybeEat(TokenKind.Multiply)) {
                names = (string[])FromImportStatement.Star;
                asNames = null;
            } else {
                List<string> l = new List<string>();
                List<string> las = new List<string>();

                if (MaybeEat(TokenKind.LeftParenthesis)) {
                    ParseAsNameList(l, las);
                    Eat(TokenKind.RightParenthesis);
                } else {
                    ParseAsNameList(l, las);
                }
                names = l.ToArray();
                asNames = las.ToArray();
            }

            // Process from __future__ statement

            if (dname.Names.Count == 1 && dname.Names[0] == "__future__") {
                if (!_fromFutureAllowed) {
                    ReportSyntaxError(IronPython.Resources.MisplacedFuture);
                }
                if (names == FromImportStatement.Star) {
                    ReportSyntaxError(IronPython.Resources.NoFutureStar);
                }
                fromFuture = true;
                foreach (string name in names) {
                    if (name == "division") {
                        _languageFeatures |= ModuleOptions.TrueDivision;
                    } else if (name == "with_statement") {
                        _languageFeatures |= ModuleOptions.WithStatement;
                    } else if (name == "absolute_import") {
                        _languageFeatures |= ModuleOptions.AbsoluteImports;
                    } else if (name == "print_function") {
                        _languageFeatures |= ModuleOptions.PrintFunction;
                        _tokenizer.PrintFunction = true;
                    } else if (name == "unicode_literals") {
                        _tokenizer.UnicodeLiterals = true;
                        _languageFeatures |= ModuleOptions.UnicodeLiterals;
                    } else if (name == "nested_scopes") {
                    } else if (name == "generators") {
                    } else {
                        string strName = name;
                        fromFuture = false;

                        if (strName != "braces") {
                            ReportSyntaxError(IronPython.Resources.UnknownFutureFeature + strName);
                        } else {
                            // match CPython error message
                            ReportSyntaxError(IronPython.Resources.NotAChance);
                        }
                    }
                }
            }

            if (ateParen) {
                Eat(TokenKind.RightParenthesis);
            }

            FromImportStatement ret = new FromImportStatement(dname, (string[])names, asNames, fromFuture, AbsoluteImports);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        // import_as_name (',' import_as_name)*
        private void ParseAsNameList(List<string> l, List<string> las) {
            l.Add(ReadName());
            las.Add(MaybeParseAsName());
            while (MaybeEat(TokenKind.Comma)) {
                if (PeekToken(TokenKind.RightParenthesis)) return;  // the list is allowed to end with a ,
                l.Add(ReadName());
                las.Add(MaybeParseAsName());
            }
        }

        //import_as_name: NAME [NAME NAME]
        //dotted_as_name: dotted_name [NAME NAME]
        private string MaybeParseAsName() {
            if (MaybeEat(TokenKind.KeywordAs)) {
                return ReadName();
            }
            return null;
        }

        //exec_stmt: 'exec' expr ['in' expression [',' expression]]
        private ExecStatement ParseExecStmt() {
            Eat(TokenKind.KeywordExec);
            SourceLocation start = GetStart();
            Expression code, locals = null, globals = null;
            code = ParseExpr();
            if (MaybeEat(TokenKind.KeywordIn)) {
                globals = ParseExpression();
                if (MaybeEat(TokenKind.Comma)) {
                    locals = ParseExpression();
                }
            }
            ExecStatement ret = new ExecStatement(code, locals, globals);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        //global_stmt: 'global' NAME (',' NAME)*
        private GlobalStatement ParseGlobalStmt() {
            Eat(TokenKind.KeywordGlobal);
            SourceLocation start = GetStart();
            List<string> l = new List<string>();
            l.Add(ReadName());
            while (MaybeEat(TokenKind.Comma)) {
                l.Add(ReadName());
            }
            string[] names = l.ToArray();
            GlobalStatement ret = new GlobalStatement(names);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        //raise_stmt: 'raise' [expression [',' expression [',' expression]]]
        private RaiseStatement ParseRaiseStmt() {
            Eat(TokenKind.KeywordRaise);
            SourceLocation start = GetStart();
            Expression type = null, _value = null, traceback = null;

            if (!NeverTestToken(PeekToken())) {
                type = ParseExpression();
                if (MaybeEat(TokenKind.Comma)) {
                    _value = ParseExpression();
                    if (MaybeEat(TokenKind.Comma)) {
                        traceback = ParseExpression();
                    }
                }
            }
            RaiseStatement ret = new RaiseStatement(type, _value, traceback);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        //assert_stmt: 'assert' expression [',' expression]
        private AssertStatement ParseAssertStmt() {
            Eat(TokenKind.KeywordAssert);
            SourceLocation start = GetStart();
            Expression expr = ParseExpression();
            Expression message = null;
            if (MaybeEat(TokenKind.Comma)) {
                message = ParseExpression();
            }
            AssertStatement ret = new AssertStatement(expr, message);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        //print_stmt: 'print' ( [ expression (',' expression)* [','] ] | '>>' expression [ (',' expression)+ [','] ] )
        private PrintStatement ParsePrintStmt() {
            Eat(TokenKind.KeywordPrint);
            SourceLocation start = GetStart();
            Expression dest = null;
            PrintStatement ret;

            bool needNonEmptyTestList = false;
            if (MaybeEat(TokenKind.RightShift)) {
                dest = ParseExpression();
                if (MaybeEat(TokenKind.Comma)) {
                    needNonEmptyTestList = true;
                } else {
                    ret = new PrintStatement(dest, new Expression[0], false);
                    ret.SetLoc(start, GetEnd());
                    return ret;
                }
            }

            bool trailingComma;
            List<Expression> l = ParseExpressionList(out trailingComma);
            if (needNonEmptyTestList && l.Count == 0) {
                ReportSyntaxError(_lookahead);
            }
            Expression[] exprs = l.ToArray();
            ret = new PrintStatement(dest, exprs, trailingComma);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        private string SetPrivatePrefix(string name) {
            string oldPrefix = _privatePrefix;

            _privatePrefix = GetPrivatePrefix(name);

            return oldPrefix;
        }

        internal static string GetPrivatePrefix(string name) {
            // Remove any leading underscores before saving the prefix
            if (name != null) {
                for (int i = 0; i < name.Length; i++) {
                    if (name[i] != '_') {
                        return name.Substring(i);
                    }
                }
            }
            // Name consists of '_'s only, no private prefix mapping
            return null;
        }

        private ErrorExpression Error() {
            var res = new ErrorExpression();
            res.SetLoc(GetStart(), GetEnd());
            return res;
        }

        private ExpressionStatement ErrorStmt() {
            return new ExpressionStatement(Error());
        }

        //classdef: 'class' NAME ['(' testlist ')'] ':' suite
        private ClassDefinition ParseClassDef() {
            Eat(TokenKind.KeywordClass);

            SourceLocation start = GetStart();
            string name = ReadName();
            if (name == null) {
                // no name, assume there's no class.
                return new ClassDefinition(null, new Expression[0], ErrorStmt());
            }

            Expression[] bases = new Expression[0];
            if (MaybeEat(TokenKind.LeftParenthesis)) {
                List<Expression> l = ParseTestList();

                if (l.Count == 1 && l[0] is ErrorExpression) {
                    // error handling, classes is incomplete.
                    return new ClassDefinition(name, new Expression[0], ErrorStmt());
                }
                bases = l.ToArray();
                Eat(TokenKind.RightParenthesis);
            }
            SourceLocation mid = GetEnd();

            // Save private prefix
            string savedPrefix = SetPrivatePrefix(name);

            // Parse the class body
            Statement body = ParseClassOrFuncBody();

            // Restore the private prefix
            _privatePrefix = savedPrefix;

            ClassDefinition ret = new ClassDefinition(name, bases, body);
            ret.Header = mid;
            ret.SetLoc(start, GetEnd());
            return ret;
        }


        //  decorators ::=
        //      decorator+
        //  decorator ::=
        //      "@" dotted_name ["(" [argument_list [","]] ")"] NEWLINE
        private List<Expression> ParseDecorators() {
            List<Expression> decorators = new List<Expression>();

            while (MaybeEat(TokenKind.At)) {
                SourceLocation start = GetStart();
                Expression decorator = new NameExpression(ReadName());
                decorator.SetLoc(start, GetEnd());
                while (MaybeEat(TokenKind.Dot)) {
                    string name = ReadNameMaybeNone();
                    decorator = new MemberExpression(decorator, name);
                    decorator.SetLoc(GetStart(), GetEnd());
                }
                decorator.SetLoc(start, GetEnd());

                if (MaybeEat(TokenKind.LeftParenthesis)) {
                    _sink.StartParameters(GetSpan());
                    Arg[] args = FinishArgumentList(null);
                    decorator = FinishCallExpr(decorator, args);
                }
                decorator.SetLoc(start, GetEnd());
                EatNewLine();

                decorators.Add(decorator);
            }

            return decorators;
        }

        // funcdef: [decorators] 'def' NAME parameters ':' suite
        // 2.6: 
        //  decorated: decorators (classdef | funcdef)
        // this gets called with "@" look-ahead
        private Statement ParseDecorated() {
            List<Expression> decorators = ParseDecorators();

            Statement res;

            if (PeekToken() == Tokens.KeywordDefToken) {
                FunctionDefinition fnc = ParseFuncDef();
                fnc.Decorators = decorators.ToArray();
                res = fnc;
            } else if (PeekToken() == Tokens.KeywordClassToken) {
                ClassDefinition cls = ParseClassDef();
                cls.Decorators = decorators.ToArray();
                res = cls;
            } else {
                res = new EmptyStatement();
                ReportSyntaxError(_lookahead);
            }
            
            return res;
        }

        // funcdef: [decorators] 'def' NAME parameters ':' suite
        // parameters: '(' [varargslist] ')'
        // this gets called with "def" as the look-ahead
        private FunctionDefinition ParseFuncDef() {
            Eat(TokenKind.KeywordDef);
            SourceLocation start = GetStart();
            string name = ReadName();

            Eat(TokenKind.LeftParenthesis);

            SourceLocation lStart = GetStart(), lEnd = GetEnd();
            int grouping = _tokenizer.GroupingLevel;

            Parameter[] parameters = ParseVarArgsList(TokenKind.RightParenthesis);
            FunctionDefinition ret;
            if (parameters == null) {
                // error in parameters
                ret = new FunctionDefinition(name, new Parameter[0]);
                ret.SetLoc(start, lEnd);
                return ret;
            }
            
            SourceLocation rStart = GetStart(), rEnd = GetEnd();

            ret = new FunctionDefinition(name, parameters);
            PushFunction(ret);


            Statement body = ParseClassOrFuncBody();
            FunctionDefinition ret2 = PopFunction();
            System.Diagnostics.Debug.Assert(ret == ret2);

            ret.Body = body;
            ret.Header = rEnd;

            _sink.MatchPair(new SourceSpan(lStart, lEnd), new SourceSpan(rStart, rEnd), grouping);

            ret.SetLoc(start, body.End);

            return ret;
        }

        private Parameter ParseParameterName(Dictionary<string, object> names, ParameterKind kind) {
            string name = ReadName();
            if (name != null) {
                CheckUniqueParameter(names, name);
            } else {
                return null;
            }
            Parameter parameter = new Parameter(name, kind);
            parameter.SetLoc(GetStart(), GetEnd());
            return parameter;
        }

        private void CheckUniqueParameter(Dictionary<string, object> names, string name) {
            if (names.ContainsKey(name)) {
                ReportSyntaxError(String.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    Resources.DuplicateArgumentInFuncDef,
                    name));
            }
            names[name] = null;
        }

        //varargslist: (fpdef ['=' expression ] ',')* ('*' NAME [',' '**' NAME] | '**' NAME) | fpdef ['=' expression] (',' fpdef ['=' expression])* [',']
        //fpdef: NAME | '(' fplist ')'
        //fplist: fpdef (',' fpdef)* [',']
        private Parameter[] ParseVarArgsList(TokenKind terminator) {
            // parameters not doing * or ** today
            List<Parameter> pl = new List<Parameter>();
            Dictionary<string, object> names = new Dictionary<string, object>(StringComparer.Ordinal);
            bool needDefault = false;
            for (int position = 0; ; position++) {
                if (MaybeEat(terminator)) break;

                Parameter parameter;

                if (MaybeEat(TokenKind.Multiply)) {
                    parameter = ParseParameterName(names, ParameterKind.List);
                    if (parameter == null) {
                        // no parameter name, syntax error
                        return null;
                    }
                    pl.Add(parameter);
                    if (MaybeEat(TokenKind.Comma)) {
                        Eat(TokenKind.Power);
                        parameter = ParseParameterName(names, ParameterKind.Dictionary);
                        if (parameter == null) {
                            return null;
                        }
                        pl.Add(parameter);
                    }
                    Eat(terminator);
                    break;
                } else if (MaybeEat(TokenKind.Power)) {
                    parameter = ParseParameterName(names, ParameterKind.Dictionary);
                    if (parameter == null) {
                        // no parameter name, syntax error
                        return null;
                    }
                    pl.Add(parameter);
                    Eat(terminator);
                    break;
                }

                //
                //  Parsing defparameter:
                //
                //  defparameter ::=
                //      parameter ["=" expression]

                if ((parameter = ParseParameter(position, names)) != null) {
                    pl.Add(parameter);
                    if (MaybeEat(TokenKind.Assign)) {
                        needDefault = true;
                        parameter.DefaultValue = ParseExpression();
                    } else if (needDefault) {
                        ReportSyntaxError(IronPython.Resources.DefaultRequired);
                    }
                } else {
                    // no parameter due to syntax error
                    return null;
                }

                if (!MaybeEat(TokenKind.Comma)) {
                    Eat(terminator);
                    break;
                }
            }

            return pl.ToArray();
        }

        //  parameter ::=
        //      identifier | "(" sublist ")"
        private Parameter ParseParameter(int position, Dictionary<string, object> names) {
            Token t = PeekToken();
            Parameter parameter = null;

            switch (t.Kind) {
                case TokenKind.LeftParenthesis: // sublist
                    NextToken();
                    Expression ret = ParseSublist(names);
                    Eat(TokenKind.RightParenthesis);
                    TupleExpression tret = ret as TupleExpression;
                    NameExpression nameRet;

                    if (tret != null) {
                        parameter = new SublistParameter(position, tret);
                    } else if ((nameRet = ret as NameExpression) != null) {
                        parameter = new Parameter(nameRet.Name);
                    } else {
                        ReportSyntaxError(_lookahead);
                    }

                    if (parameter != null) {
                        parameter.SetLoc(ret.Span);
                    }
                    break;

                case TokenKind.Name:  // identifier
                    NextToken();
                    string name = FixName((string)t.Value);
                    parameter = new Parameter(name);
                    CompleteParameterName(parameter, name, names);
                    break;

                default:
                    ReportSyntaxError(_lookahead);
                    break;
            }

            return parameter;
        }

        private void CompleteParameterName(Node node, string name, Dictionary<string, object> names) {
            SourceSpan span = GetSpan();
            _sink.StartName(span, name);
            CheckUniqueParameter(names, name);
            node.SetLoc(span);
        }

        //  parameter ::=
        //      identifier | "(" sublist ")"
        private Expression ParseSublistParameter(Dictionary<string, object> names) {
            Token t = NextToken();
            Expression ret = null;
            switch (t.Kind) {
                case TokenKind.LeftParenthesis: // sublist
                    ret = ParseSublist(names);
                    Eat(TokenKind.RightParenthesis);
                    break;
                case TokenKind.Name:  // identifier
                    string name = FixName((string)t.Value);
                    NameExpression ne = new NameExpression(name);
                    CompleteParameterName(ne, name, names);
                    return ne;
                default:
                    ReportSyntaxError(_token);
                    ret = Error();
                    break;
            }
            return ret;
        }

        //  sublist ::=
        //      parameter ("," parameter)* [","]
        private Expression ParseSublist(Dictionary<string, object> names) {
            bool trailingComma;
            List<Expression> list = new List<Expression>();
            for (; ; ) {
                trailingComma = false;
                list.Add(ParseSublistParameter(names));
                if (MaybeEat(TokenKind.Comma)) {
                    trailingComma = true;
                    switch (PeekToken().Kind) {
                        case TokenKind.LeftParenthesis:
                        case TokenKind.Name:
                            continue;
                        default:
                            break;
                    }
                    break;
                } else {
                    trailingComma = false;
                    break;
                }
            }
            return MakeTupleOrExpr(list, trailingComma);
        }

        //Python2.5 -> old_lambdef: 'lambda' [varargslist] ':' old_expression
        private Expression FinishOldLambdef() {
            FunctionDefinition func = ParseLambdaHelperStart(null);
            Expression expr = ParseOldExpression();
            return ParseLambdaHelperEnd(func, expr);
        }

        //lambdef: 'lambda' [varargslist] ':' expression
        private Expression FinishLambdef() {
            FunctionDefinition func = ParseLambdaHelperStart(null);
            Expression expr = ParseExpression();
            return ParseLambdaHelperEnd(func, expr);
        }


        // Helpers for parsing lambda expressions. 
        // Usage
        //   FunctionDefinition f = ParseLambdaHelperStart(string);
        //   Expression expr = ParseXYZ();
        //   return ParseLambdaHelperEnd(f, expr);
        private FunctionDefinition ParseLambdaHelperStart(string name) {
            SourceLocation start = GetStart();
            Parameter[] parameters;
            parameters = ParseVarArgsList(TokenKind.Colon);
            SourceLocation mid = GetEnd();

            FunctionDefinition func = new FunctionDefinition(name, parameters);
            func.Header = mid;
            func.Start = start;

            // Push the lambda function on the stack so that it's available for any yield expressions to mark it as a generator.
            PushFunction(func);

            return func;
        }

        private Expression ParseLambdaHelperEnd(FunctionDefinition func, Expression expr) {
            // Pep 342 in Python 2.5 allows Yield Expressions, which can occur inside a Lambda body. 
            // In this case, the lambda is a generator and will yield it's final result instead of just return it.
            Statement body;
            if (func.IsGenerator) {
                YieldExpression y = new YieldExpression(expr);
                y.SetLoc(expr.Span);
                body = new ExpressionStatement(y);
            } else {
                body = new ReturnStatement(expr);
            }
            body.SetLoc(expr.Start, expr.End);

            FunctionDefinition func2 = PopFunction();
            System.Diagnostics.Debug.Assert(func == func2);

            func.Body = body;
            func.End = GetEnd();

            LambdaExpression ret = new LambdaExpression(func);
            ret.SetLoc(func.Span);
            return ret;
        }

        //while_stmt: 'while' expression ':' suite ['else' ':' suite]
        private WhileStatement ParseWhileStmt() {
            Eat(TokenKind.KeywordWhile);
            SourceLocation start = GetStart();
            Expression expr = ParseExpression();
            SourceLocation mid = GetEnd();
            Statement body = ParseLoopSuite();
            Statement else_ = null;
            if (MaybeEat(TokenKind.KeywordElse)) {
                else_ = ParseSuite();
            }
            WhileStatement ret = new WhileStatement(expr, body, else_);
            ret.SetLoc(start, mid, GetEnd());
            return ret;
        }

        //with_stmt: 'with' expression [ 'as' with_var ] ':' suite
        private WithStatement ParseWithStmt() {
            Eat(TokenKind.KeywordWith);
            SourceLocation start = GetStart();
            Expression contextManager = ParseExpression();
            Expression var = null;
            if (MaybeEat(TokenKind.KeywordAs)) {
                var = ParseExpression();
            }

            SourceLocation header = GetEnd();
            Statement body = ParseSuite();
            WithStatement ret = new WithStatement(contextManager, var, body);
            ret.Header = header;
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        //for_stmt: 'for' target_list 'in' expression_list ':' suite ['else' ':' suite]
        private ForStatement ParseForStmt() {
            Eat(TokenKind.KeywordFor);
            SourceLocation start = GetStart();

            bool trailingComma;
            List<Expression> l = ParseTargetList(out trailingComma);

            // expr list is something like:
            //  ()
            //  a
            //  a,b
            //  a,b,c
            // we either want just () or a or we want (a,b) and (a,b,c)
            // so we can do tupleExpr.EmitSet() or loneExpr.EmitSet()

            Expression lhs = MakeTupleOrExpr(l, trailingComma);
            Eat(TokenKind.KeywordIn);
            Expression list = ParseTestListAsExpr(false);
            SourceLocation header = GetEnd();
            Statement body = ParseLoopSuite();
            Statement else_ = null;
            if (MaybeEat(TokenKind.KeywordElse)) {
                else_ = ParseSuite();
            }
            ForStatement ret = new ForStatement(lhs, list, body, else_);
            ret.Header = header;
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        private Statement ParseLoopSuite() {
            Statement body;
            bool inLoop = _inLoop;
            try {
                _inLoop = true;
                body = ParseSuite();
            } finally {
                _inLoop = inLoop;
            }
            return body;
        }

        private Statement ParseClassOrFuncBody() {
            Statement body;
            bool inLoop = _inLoop, inFinally = _inFinally, isGenerator = _isGenerator, returnWithValue = _returnWithValue;
            try {
                _inLoop = false;
                _inFinally = false;
                _isGenerator = false;
                _returnWithValue = false;
                body = ParseSuite();
            } finally {
                _inLoop = inLoop;
                _inFinally = inFinally;
                _isGenerator = isGenerator;
                _returnWithValue = returnWithValue;
            }
            return body;
        }

        // if_stmt: 'if' expression ':' suite ('elif' expression ':' suite)* ['else' ':' suite]
        private IfStatement ParseIfStmt() {
            Eat(TokenKind.KeywordIf);
            SourceLocation start = GetStart();
            List<IfStatementTest> l = new List<IfStatementTest>();
            l.Add(ParseIfStmtTest());

            while (MaybeEat(TokenKind.KeywordElseIf)) {
                l.Add(ParseIfStmtTest());
            }

            Statement else_ = null;
            if (MaybeEat(TokenKind.KeywordElse)) {
                else_ = ParseSuite();
            }

            IfStatementTest[] tests = l.ToArray();
            IfStatement ret = new IfStatement(tests, else_);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        private IfStatementTest ParseIfStmtTest() {
            SourceLocation start = GetStart();
            Expression expr = ParseExpression();
            SourceLocation header = GetEnd();
            Statement suite = ParseSuite();
            IfStatementTest ret = new IfStatementTest(expr, suite);
            ret.SetLoc(start, suite.End);
            ret.Header = header;
            return ret;
        }

        //try_stmt: ('try' ':' suite (except_clause ':' suite)+
        //    ['else' ':' suite] | 'try' ':' suite 'finally' ':' suite)
        //# NB compile.c makes sure that the default except clause is last

        // Python 2.5 grammar
        //try_stmt: 'try' ':' suite
        //          (
        //            (except_clause ':' suite)+
        //            ['else' ':' suite]
        //            ['finally' ':' suite]
        //          |
        //            'finally' : suite
        //          )


        private Statement ParseTryStatement() {
            Eat(TokenKind.KeywordTry);
            SourceLocation start = GetStart();
            SourceLocation mid = GetEnd();
            Statement body = ParseSuite();
            Statement finallySuite = null;
            Statement elseSuite = null;
            Statement ret;
            SourceLocation end;
            if (MaybeEat(TokenKind.KeywordFinally)) {
                finallySuite = ParseFinallySuite(finallySuite);
                end = finallySuite.End;
                TryStatement tfs = new TryStatement(body, null, elseSuite, finallySuite);
                tfs.Header = mid;
                ret = tfs;
            } else {
                List<TryStatementHandler> handlers = new List<TryStatementHandler>();
                TryStatementHandler dh = null;
                do {
                    TryStatementHandler handler = ParseTryStmtHandler();
                    end = handler.End;
                    handlers.Add(handler);

                    if (dh != null) {
                        ReportSyntaxError(dh.Start, dh.End, "default 'except' must be last");
                    }
                    if (handler.Test == null) {
                        dh = handler;
                    }
                } while (PeekToken().Kind == TokenKind.KeywordExcept);

                if (MaybeEat(TokenKind.KeywordElse)) {
                    elseSuite = ParseSuite();
                    end = elseSuite.End;
                }

                if (MaybeEat(TokenKind.KeywordFinally)) {
                    // If this function has an except block, then it can set the current exception.
                    finallySuite = ParseFinallySuite(finallySuite);
                    end = finallySuite.End;
                }

                TryStatement ts = new TryStatement(body, handlers.ToArray(), elseSuite, finallySuite);
                ts.Header = mid;
                ret = ts;
            }
            ret.SetLoc(start, end);
            return ret;
        }

        private Statement ParseFinallySuite(Statement finallySuite) {
            MarkFunctionContainsFinally();
            bool inFinally = _inFinally;
            try {
                _inFinally = true;
                finallySuite = ParseSuite();
            } finally {
                _inFinally = inFinally;
            }
            return finallySuite;
        }

        private void MarkFunctionContainsFinally() {
            FunctionDefinition current = CurrentFunction;
            if (current != null) {
                current.ContainsTryFinally = true;
            }
        }

        //except_clause: 'except' [expression [',' expression]]
        //2.6: except_clause: 'except' [expression [(',' or 'as') expression]]
        private TryStatementHandler ParseTryStmtHandler() {
            Eat(TokenKind.KeywordExcept);

            // If this function has an except block, then it can set the current exception.
            FunctionDefinition current = CurrentFunction;
            if (current != null) {
                current.CanSetSysExcInfo = true;
            }

            SourceLocation start = GetStart();
            Expression test1 = null, test2 = null;
            if (PeekToken().Kind != TokenKind.Colon) {
                test1 = ParseExpression();
                if (MaybeEat(TokenKind.Comma) || MaybeEat(TokenKind.KeywordAs)) {
                    test2 = ParseExpression();
                }
            }
            SourceLocation mid = GetEnd();
            Statement body = ParseSuite();
            TryStatementHandler ret = new TryStatementHandler(test1, test2, body);
            ret.Header = mid;
            ret.SetLoc(start, body.End);
            return ret;
        }

        //suite: simple_stmt NEWLINE | Newline INDENT stmt+ DEDENT
        private Statement ParseSuite() {
            if (!EatNoEof(TokenKind.Colon)) {
                // improve error handling...
                return ErrorStmt();
            }

            TokenWithSpan cur = _lookahead;
            List<Statement> l = new List<Statement>();
            
            // we only read a real NewLine here because we need to adjust error reporting
            // for the interpreter.
            if (MaybeEat(TokenKind.NewLine)) {
                CheckSuiteEofError(cur);

                // for error reporting we track the NL tokens and report the error on
                // the last one.  This matches CPython.
                cur = _lookahead;
                while (PeekToken(TokenKind.NLToken)) {
                    cur = _lookahead;
                    NextToken();
                }
                    
                if (!MaybeEat(TokenKind.Indent)) {
                    // no indent?  report the indentation error.
                    if (cur.Token.Kind == TokenKind.Dedent) {
                        ReportSyntaxError(_lookahead.Span.Start, _lookahead.Span.End, "expected an indented block", ErrorCodes.SyntaxError | ErrorCodes.IncompleteStatement);
                    } else {
                        ReportSyntaxError(cur, ErrorCodes.IndentationError);
                    }
                    return ErrorStmt();
                }

                while (true) {
                    Statement s = ParseStmt();

                    l.Add(s);
                    if (MaybeEat(TokenKind.Dedent)) break;
                    if (PeekToken().Kind == TokenKind.EndOfFile) {
                        ReportSyntaxError("unexpected end of file");
                        break; // error handling
                    }
                }
                Statement[] stmts = l.ToArray();
                SuiteStatement ret = new SuiteStatement(stmts);
                ret.SetLoc(stmts[0].Start, stmts[stmts.Length - 1].End);
                return ret;
            } else {
                //  simple_stmt NEWLINE
                //  ParseSimpleStmt takes care of the NEWLINE
                Statement s = ParseSimpleStmt();
                return s;
            }
        }

        private void CheckSuiteEofError(TokenWithSpan cur) {
            if (MaybeEat(TokenKind.EndOfFile)) {
                // for interactive parsing we allow the user to continue in this case
                ReportSyntaxError(_lookahead.Token, cur.Span, ErrorCodes.SyntaxError, true);
            }
        }

        // Python 2.5 -> old_test: or_test | old_lambdef
        private Expression ParseOldExpression() {
            if (MaybeEat(TokenKind.KeywordLambda)) {
                return FinishOldLambdef();
            }
            return ParseOrTest();
        }

        // expression: conditional_expression | lambda_form
        // conditional_expression: or_test ['if' or_test 'else' expression]
        // lambda_form: "lambda" [parameter_list] : expression
        private Expression ParseExpression() {
            if (MaybeEat(TokenKind.KeywordLambda)) {
                return FinishLambdef();
            }

            Expression ret = ParseOrTest();
            if (MaybeEat(TokenKind.KeywordIf)) {
                SourceLocation start = ret.Start;
                ret = ParseConditionalTest(ret);
                ret.SetLoc(start, GetEnd());
            }

            return ret;
        }

        // or_test: and_test ('or' and_test)*
        private Expression ParseOrTest() {
            Expression ret = ParseAndTest();
            while (MaybeEat(TokenKind.KeywordOr)) {
                SourceLocation start = ret.Start;
                ret = new OrExpression(ret, ParseAndTest());
                ret.SetLoc(start, GetEnd());
            }
            return ret;
        }

        private Expression ParseConditionalTest(Expression trueExpr) {
            Expression expr = ParseOrTest();
            Eat(TokenKind.KeywordElse);
            Expression falseExpr = ParseExpression();
            return new ConditionalExpression(expr, trueExpr, falseExpr);
        }

        // and_test: not_test ('and' not_test)*
        private Expression ParseAndTest() {
            Expression ret = ParseNotTest();
            while (MaybeEat(TokenKind.KeywordAnd)) {
                SourceLocation start = ret.Start;
                ret = new AndExpression(ret, ParseAndTest());
                ret.SetLoc(start, GetEnd());
            }
            return ret;
        }

        //not_test: 'not' not_test | comparison
        private Expression ParseNotTest() {
            if (MaybeEat(TokenKind.KeywordNot)) {
                SourceLocation start = GetStart();
                Expression ret = new UnaryExpression(PythonOperator.Not, ParseNotTest());
                ret.SetLoc(start, GetEnd());
                return ret;
            } else {
                return ParseComparison();
            }
        }
        //comparison: expr (comp_op expr)*
        //comp_op: '<'|'>'|'=='|'>='|'<='|'<>'|'!='|'in'|'not' 'in'|'is'|'is' 'not'
        private Expression ParseComparison() {
            Expression ret = ParseExpr();
            while (true) {
                PythonOperator op;
                switch (PeekToken().Kind) {
                    case TokenKind.LessThan: NextToken(); op = PythonOperator.LessThan; break;
                    case TokenKind.LessThanOrEqual: NextToken(); op = PythonOperator.LessThanOrEqual; break;
                    case TokenKind.GreaterThan: NextToken(); op = PythonOperator.GreaterThan; break;
                    case TokenKind.GreaterThanOrEqual: NextToken(); op = PythonOperator.GreaterThanOrEqual; break;
                    case TokenKind.Equals: NextToken(); op = PythonOperator.Equal; break;
                    case TokenKind.NotEquals: NextToken(); op = PythonOperator.NotEqual; break;
                    case TokenKind.LessThanGreaterThan: NextToken(); op = PythonOperator.NotEqual; break;

                    case TokenKind.KeywordIn: NextToken(); op = PythonOperator.In; break;
                    case TokenKind.KeywordNot: NextToken(); Eat(TokenKind.KeywordIn); op = PythonOperator.NotIn; break;

                    case TokenKind.KeywordIs:
                        NextToken();
                        if (MaybeEat(TokenKind.KeywordNot)) {
                            op = PythonOperator.IsNot;
                        } else {
                            op = PythonOperator.Is;
                        }
                        break;
                    default:
                        return ret;
                }
                Expression rhs = ParseComparison();
                BinaryExpression be = new BinaryExpression(op, ret, rhs);
                be.SetLoc(ret.Start, GetEnd());
                ret = be;
            }
        }

        /*
        expr: xor_expr ('|' xor_expr)*
        xor_expr: and_expr ('^' and_expr)*
        and_expr: shift_expr ('&' shift_expr)*
        shift_expr: arith_expr (('<<'|'>>') arith_expr)*
        arith_expr: term (('+'|'-') term)*
        term: factor (('*'|'/'|'%'|'//') factor)*
        */
        private Expression ParseExpr() {
            return ParseExpr(0);
        }

        private Expression ParseExpr(int precedence) {
            Expression ret = ParseFactor();
            while (true) {
                Token t = PeekToken();
                OperatorToken ot = t as OperatorToken;
                if (ot == null) return ret;

                int prec = ot.Precedence;
                if (prec >= precedence) {
                    NextToken();
                    Expression right = ParseExpr(prec + 1);
                    SourceLocation start = ret.Start;
                    ret = new BinaryExpression(GetBinaryOperator(ot), ret, right);
                    ret.SetLoc(start, GetEnd());
                } else {
                    return ret;
                }
            }
        }

        // factor: ('+'|'-'|'~') factor | power
        private Expression ParseFactor() {
            SourceLocation start = _lookahead.Span.Start;
            Expression ret;
            switch (PeekToken().Kind) {
                case TokenKind.Add:
                    NextToken();
                    ret = new UnaryExpression(PythonOperator.Pos, ParseFactor());
                    break;
                case TokenKind.Subtract:
                    NextToken();
                    ret = FinishUnaryNegate();
                    break;
                case TokenKind.Twiddle:
                    NextToken();
                    ret = new UnaryExpression(PythonOperator.Invert, ParseFactor());
                    break;
                default:
                    return ParsePower();
            }
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        private Expression FinishUnaryNegate() {
            // Special case to ensure that System.Int32.MinValue is an int and not a BigInteger
            if (PeekToken().Kind == TokenKind.Constant) {
                Token t = PeekToken();

                if (t.Value is BigInteger) {
                    BigInteger bi = (BigInteger)t.Value;
                    uint iVal;
                    if (bi.AsUInt32(out iVal) && iVal == 0x80000000) {
                        string tokenString = _tokenizer.GetTokenString(); ;
                        Debug.Assert(tokenString.Length > 0);

                        if (tokenString[tokenString.Length - 1] != 'L' &&
                            tokenString[tokenString.Length - 1] != 'l') {
                            NextToken();
                            return new ConstantExpression(-2147483648);

                        }
                    }
                }
            }

            return new UnaryExpression(PythonOperator.Negate, ParseFactor());
        }

        // power: atom trailer* ['**' factor]
        private Expression ParsePower() {
            Expression ret = ParsePrimary();
            ret = AddTrailers(ret);
            if (MaybeEat(TokenKind.Power)) {
                SourceLocation start = ret.Start;
                ret = new BinaryExpression(PythonOperator.Power, ret, ParseFactor());
                ret.SetLoc(start, GetEnd());
            }
            return ret;
        }


        // primary: atom | attributeref | subscription | slicing | call 
        // atom:    identifier | literal | enclosure 
        // enclosure: 
        //      parenth_form | 
        //      list_display | 
        //      generator_expression | 
        //      dict_display | 
        //      string_conversion | 
        //      yield_atom 
        private Expression ParsePrimary() {
            Token t = PeekToken();
            Expression ret;
            switch (t.Kind) {
                case TokenKind.LeftParenthesis: // parenth_form, generator_expression, yield_atom
                    NextToken();
                    return FinishTupleOrGenExp();
                case TokenKind.LeftBracket:     // list_display
                    NextToken();
                    return FinishListValue();
                case TokenKind.LeftBrace:       // dict_display
                    NextToken();
                    return FinishDictValue();
                case TokenKind.BackQuote:       // string_conversion
                    NextToken();
                    return FinishStringConversion();
                case TokenKind.Name:            // identifier
                    NextToken();
                    SourceSpan span = GetSpan();
                    string name = (string)t.Value;
                    _sink.StartName(span, name);
                    ret = new NameExpression(FixName(name));
                    ret.SetLoc(GetStart(), GetEnd());
                    return ret;
                case TokenKind.Constant:        // literal
                    NextToken();
                    SourceLocation start = GetStart();
                    object cv = t.Value;
                    string cvs = cv as string;
                    if (cvs != null) {
                        cv = FinishStringPlus(cvs);
                    } else {
                        Bytes bytes = cv as Bytes;
                        if (bytes != null) {
                            cv = FinishBytesPlus(bytes);
                        }
                    }

                    ret = new ConstantExpression(cv);
                    ret.SetLoc(start, GetEnd());
                    return ret;
                default:
                    ReportSyntaxError(_lookahead.Token, _lookahead.Span, ErrorCodes.SyntaxError, _allowIncomplete || _tokenizer.EndContinues);

                    // error node
                    ret = new ErrorExpression();
                    ret.SetLoc(_lookahead.Span.Start, _lookahead.Span.End);
                    return ret;
            }
        }

        private string FinishStringPlus(string s) {
            Token t = PeekToken();
            while (true) {
                if (t is ConstantValueToken) {
                    string cvs;
                    if ((cvs = t.Value as String) != null) {
                        s += cvs;
                        NextToken();
                        t = PeekToken();
                        continue;
                    } else {
                        ReportSyntaxError("invalid syntax");
                    }
                }
                break;
            }
            return s;
        }

        private Bytes FinishBytesPlus(Bytes s) {
            Token t = PeekToken();
            while (true) {
                if (t is ConstantValueToken) {
                    Bytes cvs;
                    if ((cvs = t.Value as Bytes) != null) {
                        s = s + cvs;
                        NextToken();
                        t = PeekToken();
                        continue;
                    } else {
                        ReportSyntaxError("invalid syntax");
                    }
                }
                break;
            }
            return s;
        }

        private Expression AddTrailers(Expression ret) {
            return AddTrailers(ret, true);
        }

        // trailer: '(' [ arglist_genexpr ] ')' | '[' subscriptlist ']' | '.' NAME
        private Expression AddTrailers(Expression ret, bool allowGeneratorExpression) {
            bool prevAllow = _allowIncomplete;
            try {
                _allowIncomplete = true;
                while (true) {
                    switch (PeekToken().Kind) {
                        case TokenKind.LeftParenthesis:
                            if (!allowGeneratorExpression) return ret;

                            NextToken();
                            Arg[] args = FinishArgListOrGenExpr();
                            if (args == null) {
                                return new CallExpression(ret, new Arg[0]);
                            }

                            CallExpression call = FinishCallExpr(ret, args);
                            call.SetLoc(ret.Start, GetEnd());
                            ret = call;
                            break;
                        case TokenKind.LeftBracket:
                            NextToken();
                            Expression index = ParseSubscriptList();
                            IndexExpression ie = new IndexExpression(ret, index);
                            ie.SetLoc(ret.Start, GetEnd());
                            ret = ie;
                            break;
                        case TokenKind.Dot:
                            NextToken();
                            string name = ReadNameMaybeNone();
                            MemberExpression fe = new MemberExpression(ret, name);
                            fe.SetLoc(ret.Start, GetEnd());
                            ret = fe;
                            break;
                        case TokenKind.Constant:
                            // abc.1, abc"", abc 1L, abc 0j
                            ReportSyntaxError("invalid syntax");
                            return Error();
                        default:
                            return ret;
                    }
                }
            } finally {
                _allowIncomplete = prevAllow;
            }
        }

        //subscriptlist: subscript (',' subscript)* [',']
        //subscript: '.' '.' '.' | expression | [expression] ':' [expression] [sliceop]
        //sliceop: ':' [expression]
        private Expression ParseSubscriptList() {
            const TokenKind terminator = TokenKind.RightBracket;
            SourceLocation start0 = GetStart();
            bool trailingComma = false;

            List<Expression> l = new List<Expression>();
            while (true) {
                Expression e;
                if (MaybeEat(TokenKind.Dot)) {
                    SourceLocation start = GetStart();
                    Eat(TokenKind.Dot); Eat(TokenKind.Dot);
                    e = new ConstantExpression(Ellipsis.Value);
                    e.SetLoc(start, GetEnd());
                } else if (MaybeEat(TokenKind.Colon)) {
                    e = FinishSlice(null, GetStart());
                } else {
                    e = ParseExpression();
                    if (MaybeEat(TokenKind.Colon)) {
                        e = FinishSlice(e, e.Start);
                    }
                }

                l.Add(e);
                if (!MaybeEat(TokenKind.Comma)) {
                    Eat(terminator);
                    trailingComma = false;
                    break;
                }

                trailingComma = true;
                if (MaybeEat(terminator)) {
                    break;
                }
            }
            Expression ret = MakeTupleOrExpr(l, trailingComma, true);
            ret.SetLoc(start0, GetEnd());
            return ret;
        }

        private Expression ParseSliceEnd() {
            Expression e2 = null;
            switch (PeekToken().Kind) {
                case TokenKind.Comma:
                case TokenKind.RightBracket:
                    break;
                default:
                    e2 = ParseExpression();
                    break;
            }
            return e2;
        }

        private Expression FinishSlice(Expression e0, SourceLocation start) {
            Expression e1 = null;
            Expression e2 = null;
            bool stepProvided = false;

            switch (PeekToken().Kind) {
                case TokenKind.Comma:
                case TokenKind.RightBracket:
                    break;
                case TokenKind.Colon:
                    // x[?::?]
                    stepProvided = true;
                    NextToken();
                    e2 = ParseSliceEnd();
                    break;
                default:
                    // x[?:val:?]
                    e1 = ParseExpression();
                    if (MaybeEat(TokenKind.Colon)) {
                        stepProvided = true;
                        e2 = ParseSliceEnd();
                    }
                    break;
            }
            SliceExpression ret = new SliceExpression(e0, e1, e2, stepProvided);
            ret.SetLoc(start, GetEnd());
            return ret;
        }


        //exprlist: expr (',' expr)* [',']
        private List<Expression> ParseExprList() {
            List<Expression> l = new List<Expression>();
            while (true) {
                Expression e = ParseExpr();
                l.Add(e);
                if (!MaybeEat(TokenKind.Comma)) {
                    break;
                }
                if (NeverTestToken(PeekToken())) {
                    break;
                }
            }
            return l;
        }

        // arglist:
        //             expression                     rest_of_arguments
        //             expression "=" expression      rest_of_arguments
        //             expression "for" gen_expr_rest
        //
        private Arg[] FinishArgListOrGenExpr() {
            Arg a = null;

            _sink.StartParameters(GetSpan());

            Token t = PeekToken();
            if (t.Kind != TokenKind.RightParenthesis && t.Kind != TokenKind.Multiply && t.Kind != TokenKind.Power) {
                SourceLocation start = GetStart();
                Expression e = ParseExpression();
                if (e is ErrorExpression) {
                    return null;
                }

                if (MaybeEat(TokenKind.Assign)) {               //  Keyword argument
                    a = FinishKeywordArgument(e);

                    if (a == null) {                            // Error recovery
                        a = new Arg(e);
                        a.SetLoc(e.Start, GetEnd());
                    }
                } else if (PeekToken(Tokens.KeywordForToken)) {    //  Generator expression
                    a = new Arg(ParseGeneratorExpression(e));
                    Eat(TokenKind.RightParenthesis);
                    a.SetLoc(start, GetEnd());
                    _sink.EndParameters(GetSpan());
                    return new Arg[1] { a };       //  Generator expression is the argument
                } else {
                    a = new Arg(e);
                    a.SetLoc(e.Start, e.End);
                }

                //  Was this all?
                //
                if (MaybeEat(TokenKind.Comma)) {
                    _sink.NextParameter(GetSpan());
                } else {
                    Eat(TokenKind.RightParenthesis);
                    a.SetLoc(start, GetEnd());
                    _sink.EndParameters(GetSpan());
                    return new Arg[1] { a };
                }
            }

            return FinishArgumentList(a);
        }

        private Arg FinishKeywordArgument(Expression t) {
            NameExpression n = t as NameExpression;
            if (n == null) {
                ReportSyntaxError(IronPython.Resources.ExpectedName);
                Arg arg = new Arg(null, t);
                arg.SetLoc(t.Start, t.End);
                return arg;
            } else {
                Expression val = ParseExpression();
                Arg arg = new Arg(n.Name, val);
                arg.SetLoc(n.Start, val.End);
                return arg;
            }
        }

        private void CheckUniqueArgument(Dictionary<string, string> names, Arg arg) {
            if (arg != null && arg.Name != null) {
                string name = arg.Name;
                if (names.ContainsKey(name)) {
                    ReportSyntaxError(IronPython.Resources.DuplicateKeywordArg);
                }
                names[name] = name;
            }
        }

        //arglist: (argument ',')* (argument [',']| '*' expression [',' '**' expression] | '**' expression)
        //argument: [expression '='] expression    # Really [keyword '='] expression
        private Arg[] FinishArgumentList(Arg first) {
            const TokenKind terminator = TokenKind.RightParenthesis;
            List<Arg> l = new List<Arg>();
            Dictionary<string, string> names = new Dictionary<string, string>();

            if (first != null) {
                l.Add(first);
                CheckUniqueArgument(names, first);
            }

            // Parse remaining arguments
            while (true) {
                if (MaybeEat(terminator)) {
                    break;
                }
                SourceLocation start = GetStart();
                Arg a;
                if (MaybeEat(TokenKind.Multiply)) {
                    Expression t = ParseExpression();
                    a = new Arg("*", t);
                } else if (MaybeEat(TokenKind.Power)) {
                    Expression t = ParseExpression();
                    a = new Arg("**", t);
                } else {
                    Expression e = ParseExpression();
                    if (MaybeEat(TokenKind.Assign)) {
                        a = FinishKeywordArgument(e);
                        CheckUniqueArgument(names, a);
                    } else {
                        a = new Arg(e);
                    }
                }
                a.SetLoc(start, GetEnd());
                l.Add(a);
                if (MaybeEat(TokenKind.Comma)) {
                    _sink.NextParameter(GetSpan());
                } else {
                    Eat(terminator);
                    break;
                }
            }

            _sink.EndParameters(GetSpan());

            Arg[] ret = l.ToArray();
            return ret;

        }

        private List<Expression> ParseTestList() {
            bool tmp;
            return ParseExpressionList(out tmp);
        }

        private Expression ParseOldExpressionListAsExpr() {
            bool trailingComma;
            List<Expression> l = ParseOldExpressionList(out trailingComma);
            //  the case when no expression was parsed e.g. when we have an empty expression list
            if (l.Count == 0 && !trailingComma) {
                ReportSyntaxError("invalid syntax");
            }
            return MakeTupleOrExpr(l, trailingComma);
        }

        // old_expression_list: old_expression [(',' old_expression)+ [',']]
        private List<Expression> ParseOldExpressionList(out bool trailingComma) {
            List<Expression> l = new List<Expression>();
            trailingComma = false;
            while (true) {
                if (NeverTestToken(PeekToken())) break;
                l.Add(ParseOldExpression());
                if (!MaybeEat(TokenKind.Comma)) {
                    trailingComma = false;
                    break;
                }
                trailingComma = true;
            }
            return l;
        }

        // target_list: target ("," target)* [","] 
        private List<Expression> ParseTargetList(out bool trailingComma) {
            List<Expression> l = new List<Expression>();
            while (true) {
                l.Add(ParseTarget());

                if (!MaybeEat(TokenKind.Comma)) {
                    trailingComma = false;
                    break;
                }

                trailingComma = true;

                if (NeverTestToken(PeekToken())) break;
            }

            return l;
        }

        // target: identifier | "(" target_list ")"  | "[" target_list "]"  | attributeref  | subscription  | slicing 
        private Expression ParseTarget() {
            Token t = PeekToken();
            switch (t.Kind) {
                case TokenKind.LeftParenthesis: // parenth_form or generator_expression
                case TokenKind.LeftBracket:     // list_display
                    Eat(t.Kind);

                    bool trailingComma;
                    Expression res = MakeTupleOrExpr(ParseTargetList(out trailingComma), trailingComma);

                    if (t.Kind == TokenKind.LeftParenthesis) {
                        Eat(TokenKind.RightParenthesis);
                    } else {
                        Eat(TokenKind.RightBracket);
                    }

                    return res;
                default:        // identifier, attribute ref, subscription, slicing
                    return AddTrailers(ParsePrimary(), false);
            }
        }

        // expression_list: expression (',' expression)* [',']
        private List<Expression> ParseExpressionList(out bool trailingComma) {
            List<Expression> l = new List<Expression>();
            trailingComma = false;
            while (true) {
                if (NeverTestToken(PeekToken())) break;
                l.Add(ParseExpression());
                if (!MaybeEat(TokenKind.Comma)) {
                    trailingComma = false;
                    break;
                }
                trailingComma = true;
            }
            return l;
        }

        private Expression ParseTestListAsExpr(bool allowEmptyExpr) {
            bool trailingComma;
            List<Expression> l = ParseExpressionList(out trailingComma);
            //  the case when no expression was parsed e.g. when we have an empty expression list
            if (!allowEmptyExpr && l.Count == 0 && !trailingComma) {
                if (MaybeEat(TokenKind.Indent)) {
                    // the error is on the next token which has a useful location, unlike the indent - note we don't have an
                    // indent if we're at an EOF.  It'a also an indentation error instead of a syntax error.
                    NextToken();
                    ReportSyntaxError(GetStart(), GetEnd(), "unexpected indent", ErrorCodes.IndentationError);
                } else {
                    ReportSyntaxError(_lookahead);
                }
            }
            return MakeTupleOrExpr(l, trailingComma);
        }

        private Expression FinishExpressionListAsExpr(Expression expr) {
            SourceLocation start = GetStart();
            bool trailingComma = true;
            List<Expression> l = new List<Expression>();
            l.Add(expr);

            while (true) {
                if (NeverTestToken(PeekToken())) break;
                expr = ParseExpression();
                l.Add(expr);
                if (!MaybeEat(TokenKind.Comma)) {
                    trailingComma = false;
                    break;
                }
                trailingComma = true;
            }

            Expression ret = MakeTupleOrExpr(l, trailingComma);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        //
        //  testlist_gexp: expression ( genexpr_for | (',' expression)* [','] )
        //
        private Expression FinishTupleOrGenExp() {
            SourceLocation lStart = GetStart();
            SourceLocation lEnd = GetEnd();
            int grouping = _tokenizer.GroupingLevel;
            bool hasRightParenthesis;

            Expression ret;
            //  Empty tuple
            if (MaybeEat(TokenKind.RightParenthesis)) {
                ret = MakeTupleOrExpr(new List<Expression>(), false);
                hasRightParenthesis = true;
            } else if (MaybeEat(TokenKind.KeywordYield)) {
                ret = ParseYieldExpression();
                Eat(TokenKind.RightParenthesis);
                hasRightParenthesis = true;
            } else {
                bool prevAllow = _allowIncomplete;
                try {
                    _allowIncomplete = true;

                    Expression expr = ParseExpression();
                    if (MaybeEat(TokenKind.Comma)) {
                        // "(" expression "," ...
                        ret = FinishExpressionListAsExpr(expr);
                    } else if (PeekToken(Tokens.KeywordForToken)) {
                        // "(" expression "for" ...
                        ret = ParseGeneratorExpression(expr);
                    } else {
                        // "(" expression ")"
                        ret = expr is ParenthesisExpression ? expr : new ParenthesisExpression(expr);
                    }
                    hasRightParenthesis = Eat(TokenKind.RightParenthesis);
                } finally {
                    _allowIncomplete = prevAllow;
                }
            }

            SourceLocation rStart = GetStart();
            SourceLocation rEnd = GetEnd();

            if (hasRightParenthesis) {
                _sink.MatchPair(new SourceSpan(lStart, lEnd), new SourceSpan(rStart, rEnd), grouping);
            }

            ret.SetLoc(lStart, rEnd);
            return ret;
        }

        //  genexpr_for  ::= "for" target_list "in" or_test [genexpr_iter]
        //  genexpr_iter ::= (genexpr_for | genexpr_if) *
        //
        //  "for" has NOT been eaten before entering this method
        private Expression ParseGeneratorExpression(Expression expr) {
            ForStatement root = ParseGenExprFor();
            Statement current = root;

            for (; ; ) {
                if (PeekToken(Tokens.KeywordForToken)) {
                    current = NestGenExpr(current, ParseGenExprFor());
                } else if (PeekToken(Tokens.KeywordIfToken)) {
                    current = NestGenExpr(current, ParseGenExprIf());
                } else {
                    // Generator Expressions have an implicit function definition and yield around their expression.
                    //  (x for i in R)
                    // becomes:
                    //   def f(): 
                    //     for i in R: yield (x)
                    ExpressionStatement ys = new ExpressionStatement(new YieldExpression(expr));
                    ys.Expression.SetLoc(expr.Span);
                    ys.SetLoc(expr.Span);
                    NestGenExpr(current, ys);
                    break;
                }
            }

            // We pass the outermost iterable in as a parameter because Python semantics
            // say that this one piece is computed at definition time rather than iteration time
            const string fname = "__generator_";
            Parameter parameter = new Parameter("__gen_$_parm__", 0);
            FunctionDefinition func = new FunctionDefinition(fname, new Parameter[] { parameter }, root);
            func.IsGenerator = true;
            func.SetLoc(root.Start, GetEnd());
            func.Header = root.End;

            //  Transform the root "for" statement
            Expression outermost = root.List;
            NameExpression ne = new NameExpression("__gen_$_parm__");
            ne.SetLoc(outermost.Span);
            root.List = ne;

            GeneratorExpression ret = new GeneratorExpression(func, outermost);
            ret.SetLoc(expr.Start, GetEnd());
            return ret;
        }

        private static Statement NestGenExpr(Statement current, Statement nested) {
            ForStatement fes = current as ForStatement;
            IfStatement ifs;
            if (fes != null) {
                fes.Body = nested;
            } else if ((ifs = current as IfStatement) != null) {
                ifs.Tests[0].Body = nested;
            }
            return nested;
        }

        // "for" target_list "in" or_test
        private ForStatement ParseGenExprFor() {
            SourceLocation start = GetStart();
            Eat(TokenKind.KeywordFor);
            bool trailingComma;
            List<Expression> l = ParseTargetList(out trailingComma);
            Expression lhs = MakeTupleOrExpr(l, trailingComma);
            Eat(TokenKind.KeywordIn);

            Expression expr = null;
            expr = ParseOrTest();

            ForStatement gef = new ForStatement(lhs, expr, null, null);
            SourceLocation end = GetEnd();
            gef.SetLoc(start, end);
            gef.Header = end;
            return gef;
        }

        //  genexpr_if: "if" old_test
        private IfStatement ParseGenExprIf() {
            SourceLocation start = GetStart();
            Eat(TokenKind.KeywordIf);
            Expression expr = ParseOldExpression();
            IfStatementTest ist = new IfStatementTest(expr, null);
            SourceLocation end = GetEnd();
            ist.Header = end;
            ist.SetLoc(start, end);
            IfStatement gei = new IfStatement(new IfStatementTest[] { ist }, null);
            gei.SetLoc(start, end);
            return gei;
        }


        // dict_display: '{' [key_datum_list] '}'
        // key_datum_list: key_datum (',' key_datum)* [","]
        // key_datum: expression ':' expression
        private Expression FinishDictValue() {
            SourceLocation oStart = GetStart();
            SourceLocation oEnd = GetEnd();

            List<SliceExpression> l = new List<SliceExpression>();
            bool prevAllow = _allowIncomplete;
            try {
                _allowIncomplete = true;
                while (true) {
                    if (MaybeEat(TokenKind.RightBrace)) {
                        break;
                    }
                    Expression e1 = ParseExpression();
                    Eat(TokenKind.Colon);
                    Expression e2 = ParseExpression();
                    SliceExpression se = new SliceExpression(e1, e2, null, false);
                    se.SetLoc(e1.Start, e2.End);
                    l.Add(se);

                    if (!MaybeEat(TokenKind.Comma)) {
                        Eat(TokenKind.RightBrace);
                        break;
                    }
                }
            } finally {
                _allowIncomplete = prevAllow;
            }

            SourceLocation cStart = GetStart();
            SourceLocation cEnd = GetEnd();

            _sink.MatchPair(new SourceSpan(oStart, oEnd), new SourceSpan(cStart, cEnd), 1);

            SliceExpression[] exprs = l.ToArray();
            DictionaryExpression ret = new DictionaryExpression(exprs);
            ret.SetLoc(oStart, cEnd);
            return ret;
        }


        // listmaker: expression ( list_for | (',' expression)* [','] )
        private Expression FinishListValue() {
            SourceLocation oStart = GetStart();
            SourceLocation oEnd = GetEnd();
            int grouping = _tokenizer.GroupingLevel;

            Expression ret;
            if (MaybeEat(TokenKind.RightBracket)) {
                ret = new ListExpression();
            } else {
                bool prevAllow = _allowIncomplete;
                try {
                    _allowIncomplete = true;
                    Expression t0 = ParseExpression();
                    if (MaybeEat(TokenKind.Comma)) {
                        List<Expression> l = ParseTestList();
                        Eat(TokenKind.RightBracket);
                        l.Insert(0, t0);
                        ret = new ListExpression(l.ToArray());
                    } else if (PeekToken(Tokens.KeywordForToken)) {
                        ret = FinishListComp(t0);
                    } else {
                        Eat(TokenKind.RightBracket);
                        ret = new ListExpression(t0);
                    }
                } finally {
                    _allowIncomplete = prevAllow;
                }
            }

            SourceLocation cStart = GetStart();
            SourceLocation cEnd = GetEnd();

            _sink.MatchPair(new SourceSpan(oStart, oEnd), new SourceSpan(cStart, cEnd), grouping);

            ret.SetLoc(oStart, cEnd);
            return ret;
        }

        // list_iter: list_for | list_if
        private ListComprehension FinishListComp(Expression item) {
            List<ListComprehensionIterator> iters = new List<ListComprehensionIterator>();
            ListComprehensionFor firstFor = ParseListCompFor();
            iters.Add(firstFor);

            while (true) {
                if (PeekToken(Tokens.KeywordForToken)) {
                    iters.Add(ParseListCompFor());
                } else if (PeekToken(Tokens.KeywordIfToken)) {
                    iters.Add(ParseListCompIf());
                } else {
                    break;
                }
            }

            Eat(TokenKind.RightBracket);
            return new ListComprehension(item, iters.ToArray());
        }

        // list_for: 'for' target_list 'in' old_expression_list [list_iter]
        private ListComprehensionFor ParseListCompFor() {
            Eat(TokenKind.KeywordFor);
            SourceLocation start = GetStart();
            bool trailingComma;
            List<Expression> l = ParseTargetList(out trailingComma);

            // expr list is something like:
            //  ()
            //  a
            //  a,b
            //  a,b,c
            // we either want just () or a or we want (a,b) and (a,b,c)
            // so we can do tupleExpr.EmitSet() or loneExpr.EmitSet()

            Expression lhs = MakeTupleOrExpr(l, trailingComma);
            Eat(TokenKind.KeywordIn);

            Expression list = null;
            list = ParseOldExpressionListAsExpr();

            ListComprehensionFor ret = new ListComprehensionFor(lhs, list);

            ret.SetLoc(start, GetEnd());
            return ret;
        }

        // list_if: 'if' old_test [list_iter]
        private ListComprehensionIf ParseListCompIf() {
            Eat(TokenKind.KeywordIf);
            SourceLocation start = GetStart();
            Expression expr = ParseOldExpression();
            ListComprehensionIf ret = new ListComprehensionIf(expr);

            ret.SetLoc(start, GetEnd());
            return ret;
        }

        private Expression FinishStringConversion() {
            Expression ret;
            SourceLocation start = GetStart();
            Expression expr = ParseTestListAsExpr(false);
            Eat(TokenKind.BackQuote);
            ret = new BackQuoteExpression(expr);
            ret.SetLoc(start, GetEnd());
            return ret;
        }

        private static Expression MakeTupleOrExpr(List<Expression> l, bool trailingComma) {
            return MakeTupleOrExpr(l, trailingComma, false);
        }

        private static Expression MakeTupleOrExpr(List<Expression> l, bool trailingComma, bool expandable) {
            if (l.Count == 1 && !trailingComma) return l[0];

            Expression[] exprs = l.ToArray();
            TupleExpression te = new TupleExpression(expandable && !trailingComma, exprs);
            if (exprs.Length > 0) {
                te.SetLoc(exprs[0].Start, exprs[exprs.Length - 1].End);
            }
            return te;
        }

        private static bool NeverTestToken(Token t) {
            switch (t.Kind) {
                case TokenKind.AddEqual:
                case TokenKind.SubtractEqual:
                case TokenKind.MultiplyEqual:
                case TokenKind.DivideEqual:
                case TokenKind.ModEqual:
                case TokenKind.BitwiseAndEqual:
                case TokenKind.BitwiseOrEqual:
                case TokenKind.ExclusiveOrEqual:
                case TokenKind.LeftShiftEqual:
                case TokenKind.RightShiftEqual:
                case TokenKind.PowerEqual:
                case TokenKind.FloorDivideEqual:

                case TokenKind.Indent:
                case TokenKind.Dedent:
                case TokenKind.NewLine:
                case TokenKind.EndOfFile:
                case TokenKind.Semicolon:

                case TokenKind.Assign:
                case TokenKind.RightBrace:
                case TokenKind.RightBracket:
                case TokenKind.RightParenthesis:

                case TokenKind.Comma:

                case TokenKind.KeywordFor:
                case TokenKind.KeywordIn:
                case TokenKind.KeywordIf:
                    return true;

                default: return false;
            }
        }

        private FunctionDefinition CurrentFunction {
            get {
                if (_functions != null && _functions.Count > 0) {
                    return _functions.Peek();
                }
                return null;
            }
        }

        private FunctionDefinition PopFunction() {
            if (_functions != null && _functions.Count > 0) {
                return _functions.Pop();
            }
            return null;
        }

        private void PushFunction(FunctionDefinition function) {
            if (_functions == null) {
                _functions = new Stack<FunctionDefinition>();
            }
            _functions.Push(function);
        }

        private CallExpression FinishCallExpr(Expression target, params Arg[] args) {
            bool hasArgsTuple = false;
            bool hasKeywordDict = false;
            int keywordCount = 0;
            int extraArgs = 0;

            foreach (Arg arg in args) {
                if (arg.Name == null) {
                    if (hasArgsTuple || hasKeywordDict || keywordCount > 0) {
                        ReportSyntaxError(IronPython.Resources.NonKeywordAfterKeywordArg);
                    }
                } else if (arg.Name == "*") {
                    if (hasArgsTuple || hasKeywordDict) {
                        ReportSyntaxError(IronPython.Resources.OneListArgOnly);
                    }
                    hasArgsTuple = true; extraArgs++;
                } else if (arg.Name == "**") {
                    if (hasKeywordDict) {
                        ReportSyntaxError(IronPython.Resources.OneKeywordArgOnly);
                    }
                    hasKeywordDict = true; extraArgs++;
                } else {
                    if (hasKeywordDict) {
                        ReportSyntaxError(IronPython.Resources.KeywordOutOfSequence);
                    }
                    keywordCount++;
                }
            }

            return new CallExpression(target, args);
        }

        #endregion

        #region IDisposable Members

        public void Dispose() {
            if (_sourceReader != null) {
                _sourceReader.Close();
            }
        }

        #endregion

        #region Implementation Details

        private PythonAst ParseFileWorker(bool makeModule, bool returnValue) {
            StartParsing();

            List<Statement> l = new List<Statement>();

            //
            // A future statement must appear near the top of the module. 
            // The only lines that can appear before a future statement are: 
            // - the module docstring (if any), 
            // - comments, 
            // - blank lines, and 
            // - other future statements. 
            // 

            MaybeEatNewLine();

            if (PeekToken(TokenKind.Constant)) {
                Statement s = ParseStmt();
                l.Add(s);
                _fromFutureAllowed = false;
                ExpressionStatement es = s as ExpressionStatement;
                if (es != null) {
                    ConstantExpression ce = es.Expression as ConstantExpression;
                    if (ce != null && ce.Value is string) {
                        // doc string
                        _fromFutureAllowed = true;
                    }
                }
            }

            MaybeEatNewLine();

            // from __future__
            if (_fromFutureAllowed) {
                while (PeekToken(Tokens.KeywordFromToken)) {
                    Statement s = ParseStmt();
                    l.Add(s);
                    FromImportStatement fis = s as FromImportStatement;
                    if (fis != null && !fis.IsFromFuture) {
                        // end of from __future__
                        break;
                    }
                }
            }

            // the end of from __future__ sequence
            _fromFutureAllowed = false;

            while (true) {
                if (MaybeEat(TokenKind.EndOfFile)) break;
                if (MaybeEatNewLine()) continue;

                Statement s = ParseStmt();
                l.Add(s);
            }

            Statement[] stmts = l.ToArray();

            if (returnValue && stmts.Length > 0) {
                ExpressionStatement exprStmt = stmts[stmts.Length - 1] as ExpressionStatement;
                if (exprStmt != null) {
                    stmts[stmts.Length - 1] = new ReturnStatement(exprStmt.Expression);
                }
            }

            SuiteStatement ret = new SuiteStatement(stmts);
            ret.SetLoc(_sourceUnit.MakeLocation(SourceLocation.MinValue), GetEnd());
            return new PythonAst(ret, makeModule, _languageFeatures, false, _context);
        }

        private Statement InternalParseInteractiveInput(out bool parsingMultiLineCmpdStmt, out bool isEmptyStmt) {
            try {
                Statement s;
                isEmptyStmt = false;
                parsingMultiLineCmpdStmt = false;

                switch (PeekToken().Kind) {
                    case TokenKind.NewLine:
                        MaybeEatNewLine();
                        Eat(TokenKind.EndOfFile);
                        if (_tokenizer.EndContinues) {
                            parsingMultiLineCmpdStmt = true;
                            _errorCode = ErrorCodes.IncompleteStatement;
                        } else {
                            isEmptyStmt = true;
                        }
                        return null;

                    case TokenKind.KeywordIf:
                    case TokenKind.KeywordWhile:
                    case TokenKind.KeywordFor:
                    case TokenKind.KeywordTry:
                    case TokenKind.At:
                    case TokenKind.KeywordDef:
                    case TokenKind.KeywordClass:
                    case TokenKind.KeywordWith:
                        parsingMultiLineCmpdStmt = true;
                        s = ParseStmt();
                        EatEndOfInput();
                        break;

                    default:
                        //  parseSimpleStmt takes care of one or more simple_stmts and the Newline
                        s = ParseSimpleStmt();
                        MaybeEatNewLine();
                        Eat(TokenKind.EndOfFile);
                        break;
                }
                return s;
            } catch (BadSourceException bse) {
                throw BadSourceError(bse);
            }
        }



        private Expression ParseTestListAsExpression() {
            StartParsing();

            Expression expression = ParseTestListAsExpr(false);
            EatEndOfInput();
            return expression;
        }

        /// <summary>
        /// Maybe eats a new line token returning true if the token was
        /// eaten.
        /// 
        /// Python always tokenizes to have only 1  new line character in a 
        /// row.  But we also craete NLToken's and ignore them except for 
        /// error reporting purposes.  This gives us the same errors as 
        /// CPython and also matches the behavior of the standard library 
        /// tokenize module.  This function eats any present NL tokens and throws
        /// them away.
        /// </summary>
        private bool MaybeEatNewLine() {
            if (MaybeEat(TokenKind.NewLine)) {
                while (MaybeEat(TokenKind.NLToken)) ;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Eats a new line token throwing if the next token isn't a new line.  
        /// 
        /// Python always tokenizes to have only 1  new line character in a 
        /// row.  But we also craete NLToken's and ignore them except for 
        /// error reporting purposes.  This gives us the same errors as 
        /// CPython and also matches the behavior of the standard library 
        /// tokenize module.  This function eats any present NL tokens and throws
        /// them away.
        /// </summary>
        private bool EatNewLine() {
            bool res = Eat(TokenKind.NewLine);
            while (MaybeEat(TokenKind.NLToken)) ;
            return res;
        }

        private Token EatEndOfInput() {
            while (MaybeEatNewLine() || MaybeEat(TokenKind.Dedent)) {
                ;
            }

            Token t = NextToken();
            if (t.Kind != TokenKind.EndOfFile) {
                ReportSyntaxError(_token);
            }
            return t;
        }

        private Exception/*!*/ BadSourceError(BadSourceException bse) {
            StreamReader sr = _sourceReader.BaseReader as StreamReader;
            if (sr != null && sr.BaseStream.CanSeek) {
                return PythonContext.ReportEncodingError(sr.BaseStream, _sourceUnit.Path);

            }
            // BUG: We have some weird stream and we can't accurately track the 
            // position where the exception came from.  There are too many levels
            // of buffering below us to re-wind and calculate the actual line number, so
            // we'll give the last line number the tokenizer was at.
            return IronPython.Runtime.Operations.PythonOps.BadSourceError(
                bse._badByte,
                new SourceSpan(_tokenizer.CurrentPosition, _tokenizer.CurrentPosition),
                _sourceUnit.Path
            );
        }

        private bool TrueDivision {
            get { return (_languageFeatures & ModuleOptions.TrueDivision) == ModuleOptions.TrueDivision; }
        }

        private bool AbsoluteImports {
            get { return (_languageFeatures & ModuleOptions.AbsoluteImports) == ModuleOptions.AbsoluteImports; }
        }

        private void StartParsing() {
            if (_parsingStarted)
                throw new InvalidOperationException("Parsing already started. Use Restart to start again.");

            _parsingStarted = true;

            FetchLookahead();
        }

        private SourceLocation GetEnd() {
            Debug.Assert(_token.Token != null, "No token fetched");
            return _token.Span.End;
        }

        private SourceLocation GetStart() {
            Debug.Assert(_token.Token != null, "No token fetched");
            return _token.Span.Start;
        }

        private SourceSpan GetSpan() {
            Debug.Assert(_token.Token != null, "No token fetched");
            return _token.Span;
        }

        private Token NextToken() {
            _token = _lookahead;
            FetchLookahead();
            return _token.Token;
        }

        private Token PeekToken() {
            return _lookahead.Token;
        }

        private void FetchLookahead() {
            _lookahead.Token = _tokenizer.GetNextToken();
            _lookahead.Span = _tokenizer.TokenSpan;
        }

        private bool PeekToken(TokenKind kind) {
            return PeekToken().Kind == kind;
        }

        private bool PeekToken(Token check) {
            return PeekToken() == check;
        }       

        private bool Eat(TokenKind kind) {
            Token next = PeekToken();
            if (next.Kind != kind) {
                ReportSyntaxError(_lookahead);
                return false;
            } else {
                NextToken();
                return true;
            }
        }

        private bool EatNoEof(TokenKind kind) {
            Token next = PeekToken();
            if (next.Kind != kind) {
                ReportSyntaxError(_lookahead.Token, _lookahead.Span, ErrorCodes.SyntaxError, false);
                return false;
            }
            NextToken();
            return true;
        }

        private bool MaybeEat(TokenKind kind) {
            if (PeekToken().Kind == kind) {
                NextToken();
                return true;
            } else {
                return false;
            }
        }

        private class TokenizerErrorSink : ErrorSink {
            private readonly Parser _parser;

            public TokenizerErrorSink(Parser parser) {
                Assert.NotNull(parser);
                _parser = parser;
            }

            public override void Add(SourceUnit sourceUnit, string message, SourceSpan span, int errorCode, Severity severity) {
                if (_parser._errorCode == 0 && (severity == Severity.Error || severity == Severity.FatalError)) {
                    _parser._errorCode = errorCode;
                }
                _parser.ErrorSink.Add(sourceUnit, message, span, errorCode, severity);
            }
        }

        #endregion
    }
}
