/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Microsoft Public License. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Microsoft Public License, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Microsoft Public License.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using Microsoft.Scripting.Utils;

namespace IronRuby.Tests {    
    public partial class Tests {
        public Tests(Driver/*!*/ driver) {
            _driver = driver;

            _methods = new Action[] {
                Scenario_Startup, // must be the first test
                
                MutableString_Factories,
                MutableString_GetHashCode,
                MutableString_IsAscii,
                MutableString_Length,
                MutableString_CompareTo,
                MutableString_Equals,
                MutableString_Append_Byte,
                MutableString_Append_Char,
                MutableString_Append,
                MutableString_Insert_Byte,
                MutableString_Insert_Char,
                MutableString_Remove_Byte,
                MutableString_Remove_Char,
                MutableString_SwitchRepr,
                MutableString_Concatenate,
                MutableString_Reverse,
                MutableString_Translate1,
                MutableString_StartsWith1,
                MutableString_IndexOf1,
                MutableString_LastIndexOf1,
                MutableString_Index1,
                MutableString_IndexRegex1,
                MutableString_Characters1,
                MutableString_Bytes1,
                MutableString_ChangeEncoding1,
                RubyArray_Ctors,
                RubyArray_Basic,
                RubyArray_Add,
                RubyArray_Remove,
                RubyArray_Insert,
                RubyArray_Misc,

                Scenario_ParserLogging,
                Scenario_RubyTokenizer1,
                Identifiers1,
                Identifiers2,
                Scenario_ParseBigInts1,
                ParseIntegers1,
                Scenario_ParseNumbers1,
                Scenario_ParseInstanceClassVariables1,
                ParseGlobalVariables1,
                ParseEscapes1,
                ParseEolns1,
                Scenario_ParseRegex1,

                Scenario_RubyCategorizer1,
                NameMangling1,
                NameMangling2,
                DelegateChainClone1,

                Constants1A,
                Constants1B,
                ConstantNames,
                Constants3,
                Constants4,
                
                OverloadResolution_Block1,
                OverloadResolution_ParamArrays1,
                OverloadResolution_Numeric1,
                AmbiguousMatch1,
                Interpreter1A,
                Interpreter1B,
                Interpreter1C,
                Interpreter1D,
                Interpreter_JumpFromFinally1,
                Interpreter_JumpFromFinally2,
                Interpreter2,
                Interpreter3,
                Interpreter4,
                Interpreter5,
                // TODO: variable shadowing 
                // Interpreter6,
                InterpreterNumeric1,
                InterpreterMethodCalls1,
                InterpreterLoops1,
                InterpreterLoops2,
                InterpreterLoops3,
                InterpreterLoops4,

                SimpleCall1,
                SimpleCall2, 
                SimpleCall3, 
                SimpleCall4, 
                SimpleCall5, 
                SimpleCall6, 
                MethodCallCaching1,
                MethodCallCaching2,
                MethodCallCaching3,
                MethodCallCaching_MethodMissing1,
                MethodCallCaching_MethodMissing2,
                MethodCallCaching_MethodMissing3,
                MethodCallCaching_MethodMissing4,
                MethodCallCaching_MethodMissing5,

                NumericLiterals1,
                NumericOps1,
                StringLiterals1,
                Escapes1,
                UnicodeEscapes1,
                UnicodeEscapes2,

                Heredoc1,
                ParsingSymbols1,
                
                KCode1,
                KCode2,

                Encoding1,
                Encoding2,
                Encoding3,
                Encoding4,
                Encoding_Host1,
                Encoding_Host2,

                AstLocations1,

                Scenario_Globals1,

                Scenario_RubyMethodMissing1, 
                Scenario_RubyMethodMissing2, 
                Scenario_RubySingletonConstants1,
                Scenario_RubyMath1,

                StringsPlus,
                Strings0,
                Strings1,
                Strings2,
                Strings3,
                Strings4,
                Strings5,
                Strings6,
                Strings7,
                Strings8,
                Strings9,
                ToSConversion1,
                ToSConversion2,
                Symbols1,
                Inspect1,
                Inspect2,
                File1,
                File_AppendBytes1,
                File_WriteBytes1,
                File_ReadLine1,
                Dir1,
                Dir2,
                
                Regex1,
                Regex2,
                RegexTransform1,
                RegexTransform2,
                RegexEscape1,
                RegexCondition1,
                RegexCondition2,
                RegexEncoding1,
                RegexEncoding2,
                
                Scenario_RubyScopeParsing,
                Scenario_RubyScopes1,
                Scenario_RubyScopes2A,
                Scenario_RubyScopes2B,
                Scenario_RubyScopes3,
                Scenario_RubyScopes4,
                Scenario_RubyScopes5,
                Scenario_RubyScopes6,

                Send1,
                Send2,
                MethodCallCaching7,
                MethodCallCaching8,
                
                AttributeAccessors1,
                AttributeAccessors2,
                AttributeAccessors3,
                
                Scenario_RubyDeclarations1,
                Scenario_RubyDeclarations1A,
                Scenario_RubyDeclarations1B,
                Scenario_RubyDeclarations1C,
                Scenario_RubyDeclarations2,
                Scenario_RubyDeclarations3,
                Scenario_RubyDeclarations4,
                Scenario_RubyInclusions1,
                Scenario_RubyClassVersions1,
                Scenario_RubyClassVersions2,
                InvokeMemberCache1,
                Scenario_RubyBlockExpressions1,
                
                UnqualifiedConstants1,
                LoadAndGlobalConstants,
                GlobalConstants1,
                ConstantCaching_Unqualified1,
                ConstantCaching_Unqualified2,
                ConstantCaching_Unqualified3,
                ConstantCaching_Unqualified4,
                ConstantCaching_Unqualified5,
                ConstantCaching_Unqualified6,
                ConstantCaching_Unqualified7,
                ConstantCaching_Unqualified_IsDefined1,
                ConstantCaching_Qualified1,
                ConstantCaching_Qualified2,
                ConstantCaching_Qualified_IsDefined1,
                ConstantCaching_Qualified_IsDefined2,
                ConstantCaching_CrossRuntime1,
                ConstantCaching_AutoUpdating1A,
                ConstantCaching_AutoUpdating1B,

                Scenario_ClassVariables1,
                Scenario_ClassVariables2,
                Scenario_RubyLocals1,
                Scenario_MethodAliases1,
                Scenario_MethodAliases2,
                Scenario_MethodUndef1,
                Scenario_MethodUndef2,
                MethodUndefExpression,
                Scenario_Assignment1,
                SetterCallValue,
                SimpleInplaceAsignmentToIndirectLeftValues1,
                MemberAssignmentExpression1,
                MemberAssignmentExpression2,
                MemberAssignmentExpression3,

                Scenario_ParallelAssignment1,
                Scenario_ParallelAssignment2,
                Scenario_ParallelAssignment4,
                Scenario_ParallelAssignment5,
                Scenario_ParallelAssignment6,
                Scenario_ParallelAssignment7,
                Scenario_ParallelAssignment8,
                Scenario_ParallelAssignment9,
                Scenario_ParallelAssignment10,

                BlockEmpty,
                RubyBlocks0,
                RubyBlocks_Params1,
                RubyBlocks_Params2,
                ProcYieldCaching1,
                ProcCallCaching1,
                ProcSelf1,
                RubyBlocks2,
                RubyBlocks3,
                RubyBlocks5,
                RubyBlocks6,
                RubyBlocks7,
                RubyBlocks8,
                RubyBlocks9,
                RubyBlocks10A,
                RubyBlocks10B,
                RubyBlocks11,
                RubyBlocks12,
                RubyBlocks13,
                RubyBlocks14A,
                RubyBlocks14B,
                RubyBlocks14C,
                RubyBlocks15,
                RubyBlocks16,
                RubyBlocks17,
                RubyBlocks18,
                BlockReturnOptimization1,
                BlockReturnOptimization2,
                BlockReturnOptimization3,
                BlockReturnOptimization4,
                BlockReturnOptimization5,
                BlockReturnOptimization6,
                BlockReturnOptimization7,
                BlockArity1,
                
                Scenario_RubyBlockArgs1,
                Scenario_RubyProcYieldArgs1,
                Scenario_RubyProcCallArgs1A,
                Scenario_RubyProcCallArgs1B,
                Scenario_RubyBlockArgs2,
                Scenario_RubyProcCallArgs2A,
                Scenario_RubyProcCallArgs2B,
                Scenario_RubyProcCallArgs2C,
                Scenario_RubyProcCallArgs2D,
                Scenario_RubyBlockArgs3,
                Scenario_RubyBlockArgs4A,
                Scenario_RubyBlockArgs4B,
                Scenario_RubyBlockArgs5,
                Scenario_RubyBlockArgs6,
                // TODO: Scenario_RubyBlockArgs7,
                Scenario_RubyBlockArgs8,
                Scenario_RubyBlockArgs9,
                Scenario_RubyBlockArgs10,
                Scenario_RubyBlockArgs11,
                Proc_RhsAndBlockArguments1,

                RubyProcs1,
                RubyProcs2,
                RubyProcArgConversion1,
                RubyProcArgConversion2,
                RubyProcArgConversion3,
                RubyProcArgConversion4,
                ProcNew1,
                ProcNew2,
                ProcNew3,
                ProcNew4,
                MethodToProc1,
                DefineMethod1,
                DefineMethod2,
                ProcPosition1,
                
                Scenario_RubyInitializers0,
                Scenario_RubyInitializers1,
                Scenario_RubyInitializers2A,
                Scenario_RubyInitializers2B,
                Scenario_RubyInitializers3,
                Scenario_RubyInitializers4A,
                Scenario_RubyInitializers4B,
                Scenario_RubyInitializers4C,
                Scenario_RubyInitializers5,
                Scenario_RubyInitializers6,
                RubyInitializersCaching1,
                RubyInitializersCaching2,
                RubyInitializersCaching3,
                RubyAllocators1,

                Scenario_RubyForLoop1,
                // TODO: Python interop: Scenario_RubyForLoop2,
                WhileLoop1,
                LoopBreak1,
                LoopBreak2,
                LoopBreak3,
                LoopRedo1,
                LoopNext1,
                MethodBreakRetryRedoNext1,
                Scenario_RubyUntilLoop1,
                Scenario_WhileLoopCondition1,
                PostTestWhile1,
                PostTestUntil1,
                WhileModifier1,
                UntilModifier1,
                WhileModifier2,
                UntilModifier2,

                RangeConditionInclusive1,
                RangeConditionExclusive1,
                RangeCondition1A,
                RangeCondition1B,
                RangeCondition1C,
                RangeCondition2,
                
                Scenario_RubyClosures1,
                Scenario_RubyParams1,
                Scenario_RubyParams2,
                Scenario_RubyReturn1,
                Scenario_RubyArrays1,
                Scenario_RubyArrays2,
                Scenario_RubyArrays3,
                Scenario_RubyArrays4,
                Scenario_RubyArrays5,
                Scenario_RubyArrays6,
                Scenario_RubyHashes1A,
                Scenario_RubyHashes1B,
                Scenario_RubyHashes1C,
                Scenario_RubyHashes2,
                Scenario_RubyHashes3,
                Scenario_RubyHashes4,
                Scenario_RubyArgSplatting1,
                Scenario_RubyArgSplatting2,
                Scenario_RubyArgSplatting3,
                Scenario_RubyArgSplatting4,
                Scenario_RubyArgSplatting5,
                Scenario_CaseSplatting1,
                Scenario_RubyBoolExpressions1,
                Scenario_RubyBoolExpressions2,
                Scenario_RubyBoolExpressions3,
                Scenario_RubyBoolExpressions4,
                Scenario_RubyBoolExpressionsWithReturn1,
                Scenario_RubyBoolExpressionsWithReturn2,
                TernaryConditionalWithJumpStatements1,
                TernaryConditionalWithJumpStatements2,
                Scenario_RubyBoolAssignment,
                Scenario_RubyIfExpression1,
                Scenario_RubyIfExpression2,
                Scenario_RubyUnlessExpression1,
                Scenario_RubyConditionalExpression1,
                ConditionalStatement1,
                ConditionalStatement2,

                Scenario_UninitializedVars1,
                Scenario_UninitializedVars2,
                InstanceVariables1,
                InstanceVariables2,
                RubyHosting_DelegateConversions,
                RubyHosting1A,
                RubyHosting1B,
                RubyHosting1C,
                RubyHosting1D,
                RubyHosting1E,
                RubyHosting1F,
                RubyHosting2,
                RubyHosting3,
                RubyHosting4,
                RubyHosting5,
                RubyHosting_Scopes1,
                CrossRuntime1,
                CrossRuntime2,

                Scenario_RubyConsole1,
                ObjectOperations1,
                ObjectOperations2,
                PythonInterop1,
                PythonInterop2,
                PythonInterop3,
                PythonInterop4,
                PythonInterop5,
                // TODO: PythonInterop_InvokeMember_Fallback1,
                PythonInterop_InvokeMember_Fallback2,
                // TODO: PythonInterop_Indexers_Fallback1,
                // TODO: PythonInterop_Operators_Fallback1,
                PythonInterop_NamedIndexers1,

                CustomTypeDescriptor1,
                CustomTypeDescriptor2,
                
                Loader_Assemblies1,

                Require1,
                RequireInterop1,
                RequireInterop2,
                Load1,
                LibraryLoader1,
                LibraryLoader2,

                ClrFields1,
                ClrTypes1,
                ClrNamespaces1,
                ClrNamespaces2,
                ClrGenerics1,
                ClrGenerics2,
                ClrGenerics3,
                ClrMethods1,
                ClrMethods2,
                ClrMethods3,
                ClrMethods4,
                ClrMembers1,
                ClrVisibility1,
                ClrVisibility2,
                ClrOverloadInheritance1,
                ClrOverloadInheritance2,
                ClrOverloadInheritance3,
                ClrOverloadInheritance4,
                ClrOverloadInheritance5,
                ClrOverloadInheritance6,
                ClrMethodEnumeration1,
                ClrMethodEnumeration2,
                ClrIndexers1,
                ClrGenericMethods1,
                ClrGenericParametersInference1,
                ClrOverloadSelection1,
                ClrOverloadSelection2,
                ClrNewSlot1,
                ClrInterfaces1,
                ClrExplicitInterfaces1,
                ClrInclude1,
                ClrNew1,
                ClrNew2,
                ClrAlias1,
                ClrEnums1, 
                ClrEnums2, 
                ClrEnums3, 
                ClrDelegates1,
                ClrDelegates2,
                ClrEvents1,
                ClrEvents2,
                ClrEvents3,
                ClrEvents4,
                ClrOverride1,
                ClrOverride2,
                ClrOverride3,
                ClrOverride4,
                ClrOverride5,
                ClrOverride6,
                ClrEventImpl1,
                ClrDetachedVirtual1,
                ClrConstructor1,
                ClrConstructor2,
                ClrConstructor3,
                ClrConstructor4,
                ClrConstructor5,
                ClrPrimitiveNumericTypes1,
                ClrArrays1,
                ClrArrays2,
                ClrChar1,
                ClrOperators1,
                ClrOperators2,
                ClrOperators3,
                ClrConversions1,
                ClrHashEqualsToString1,
                ClrHashEqualsToString2,
                ClrHashEqualsToString3,
                ClrToString1,
                ClrHashEquals4,
                ClrTypeVariance1,
                HostingDefaultOptions1,
                Interactive1,
                Interactive2,
                
                Scenario_RubyReturnValues1,
                Scenario_RubyReturnValues2,
                Scenario_RubyReturnValues3,
                Scenario_RubyReturnValues4,
                Scenario_RubyReturnValues5,
                Scenario_RubyReturnValues6,

                Scenario_RubyExceptions1,
                Scenario_RubyExceptions1A,
                Scenario_RubyExceptions2A,
                Scenario_RubyExceptions2B,
                Scenario_RubyExceptions2C,
                Scenario_RubyExceptions2D,
                Scenario_RubyExceptions3,
                Scenario_RubyExceptions4,
                Scenario_RubyExceptions5,
                Scenario_RubyExceptions6,
                Scenario_RubyExceptions7,
                Scenario_RubyExceptions8,
                Scenario_RubyExceptions9,
                Scenario_RubyExceptions10,
                Scenario_RubyExceptions11,
                Scenario_RubyExceptions12,
                Scenario_RubyExceptions12A,
                Scenario_RubyExceptions13,
                Scenario_RubyExceptions14,
                Scenario_RubyExceptions15,
                Scenario_RubyExceptions16,
                JumpFromEnsure1,
                Scenario_RubyExceptions_Globals,
                Scenario_RubyRescueStatement1,
                Scenario_RubyRescueExpression1,
                Scenario_RubyRescueExpression2,
                ExceptionArg1,
                ExceptionArg2,
                RescueSplat1,
                RescueSplat2,
                RescueSplat3,
                RescueSplat4,
                ExceptionMapping1,
                ExceptionMapping2,
                ExceptionMapping3,

                ClassVariables1,
                UnqualifiedConstants2,

                AliasMethodLookup1,
                
                UndefMethodLookup,
                MethodAdded1,
                MethodLookup1,
                Visibility1,
                Visibility2A,
                Visibility2B,
                Visibility2C,
                Visibility3,
                Visibility4,
                VisibilityCaching1,
                VisibilityCaching2,
                DefineMethodVisibility1,
                DefineMethodVisibility2A,
                DefineMethodVisibility2B,
                AliasedMethodVisibility1,
                AttributeAccessorsVisibility1,
                ModuleFunctionVisibility1,
                ModuleFunctionVisibility2,

                MethodDefinitionInDefineMethod1A,
                MethodDefinitionInDefineMethod1B,
                MethodDefinitionInDefineMethod2A,
                MethodDefinitionInDefineMethod2B,
                MethodDefinitionInModuleEval1A,
                MethodDefinitionInModuleEval1B,

                MainSingleton1,
                MainSingleton2,
                Singletons1A,
                Singletons1B,
                Singletons1C,
                Singletons1D,
                Singletons2,
                Singletons3,
                SingletonCaching1,
                SingletonCaching2A,
                SingletonCaching2B,
                SingletonCaching2C,
                Scenario_ClassVariables_Singletons,
                AllowedSingletons1,
                AllowedSingletons2,
                SingletonMethodDefinitionOnSingletons1,
                ModuleSingletons1, 
                ClassSingletons1, 
                DummySingletons1, 
                DummySingletons2, 

                Super1,
                SuperParameterless1,
                SuperParameterless2A,
                SuperParameterless2B,
                SuperParameterless3,
                Super2,
                SuperToAttribute1,
                SuperAndMethodMissing1,
                SuperAndMethodMissing2,
                SuperCaching1,
                SuperInBlocks1,
                SuperInDefineMethod1,
                SuperInDefineMethod2,
                SuperInDefineMethod3,
                SuperInDefineMethod4,
                SuperInDefineMethod5,
                SuperInTopLevelCode1,
                SuperInAliasedDefinedMethod1,

                DefinedOperator_Globals1,
                DefinedOperator_Globals2,
                DefinedOperator_Methods1,
                DefinedOperator_Methods2,
                DefinedOperator_Constants1,
                DefinedOperator_Constants2,
                DefinedOperator_Constants3,
                DefinedOperator_Expressions1,
                DefinedOperator_InstanceVariables1,
                DefinedOperator_ClassVariables1,
                DefinedOperator_ClassVariables2,
                DefinedOperator_Yield1,
                DefinedOperator_Locals1,
                DefinedOperator_Super1,

                Scenario_ModuleOps_Methods,
                Scenario_MainSingleton,

                Scenario_RubyThreads1,
                Scenario_YieldCodeGen,
                Methods1, 
                MethodDef1, 
                ToIntegerConversion1,
                ToIntToStrConversion1,
                ConvertToFixnum1,
                ProtocolCaching1,
                ProtocolCaching2,
                ProtocolCaching3,
                ProtocolCaching4,
                MethodAliasExpression,
                ClassDuplication1,
                ClassDuplication2,
                ClassDuplication3,
                ClassDuplication4,
                ClassDuplication5,
                StructDup1,
                ClassDuplication6,
                ClassDuplication7,
                Clone1,
                Dup1,
                Structs1,
                MetaModules1,
                MetaModulesDuplication1,
                Autoload1,
                ModuleFreezing1,
                ModuleFreezing2,

                // eval, binding:
                Eval1,
                Eval2,
                Eval3,
                Eval4,
                Eval5,
                Eval6,
                EvalReturn1,
                EvalReturn2,
                EvalReturn3,
                EvalReturn4,
                EvalBreak1,
                EvalBreak2,
                EvalBreak3,
                EvalRetry1,
                EvalRetry2,
                EvalRedo1,
                EvalNext1,
                LocalNames1,
                LocalNames2,
                LocalNames3,
                LocalNames4,
                LiftedParameters1,
                Binding1,
                TopLevelBinding_RubyProgram,
                EvalWithProcBinding1,
                ModuleEvalProc1,
                ModuleEvalProc2,
                ModuleEvalProc3,
                InstanceEvalProc1,
                // TODO: InstanceEvalProc2,
                ModuleInstanceEvalProc3,
                ModuleClassNew1,
                ModuleClassNew2,
                ModuleEvalString1,
                InstanceEvalString1,
                ModuleEvalString2,
                InstanceEvalString2,
                ModuleInstanceEvalString3,
                AliasInModuleEval1,
                MethodAliasInModuleEval1,
                SuperInModuleEval1,
                SuperCallInEvalWithBinding18,
                SuperCallInEvalWithBinding19,
                // TODO: SuperParameterlessEval1,
                // TODO: SuperParameterlessEval2,
                SuperInDefineMethodEval1,
                // TODO: NamesEncoding1,
                BEGIN1,
                BEGIN2,
                BEGIN3,
                SymbolToProc1,

                Backtrace1,
                Backtrace2,
                Backtrace3,
                Backtrace4,
                Backtrace5,
                Backtrace6,
                Backtrace7,

                Dlr_RubySnippet,
                Dlr_ClrSubtype,
                Dlr_MethodMissing,
                Dlr_Miscellaneous,
                Dlr_Conversions1,
                Dlr_Splatting1,
                Dlr_Indexable,
                Dlr_Number,
                Dlr_Comparable,
                Dlr_RubyObjects,
                Dlr_Methods,
                Dlr_Visibility,
                Dlr_Languages,
                Dlr_DynamicObject1, 

                Serialization1,
#if !CLR2
                ClrBigIntegerV4,
#endif
            };
        }
    }
}
