using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib.Internal.CIL;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using MethodBody = Mono.Cecil.Cil.MethodBody;
using OpCode = Mono.Cecil.Cil.OpCode;
using OpCodes = Mono.Cecil.Cil.OpCodes;
using OperandType = Mono.Cecil.Cil.OperandType;
using SRE = System.Reflection.Emit;

namespace HarmonyLib.Internal.Patching
{
    /// <summary>
    /// High-level IL code manipulator for MonoMod that allows to manipulate a method as a stream of CodeInstructions.
    /// </summary>
    internal class ILManipulator
    {
        private static readonly Dictionary<short, SRE.OpCode> SREOpCodes = new Dictionary<short, SRE.OpCode>();

        static ILManipulator()
        {
            foreach (var field in typeof(SRE.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var sreOpCode = (SRE.OpCode) field.GetValue(null);
                SREOpCodes[sreOpCode.Value] = sreOpCode;
            }
        }

        private IEnumerable<CodeInstruction> codeInstructions;
        private List<MethodInfo> transpilers = new List<MethodInfo>();

        /// <summary>
        /// Initialize IL transpiler
        /// </summary>
        /// <param name="body">Body of the method to transpile</param>
        /// <param name="original">Original method. Used to resolve locals and parameters</param>
        public ILManipulator(MethodBody body, MethodBase original = null)
        {
            codeInstructions = ReadBody(body, original);
        }

        private List<CodeInstruction> ReadBody(MethodBody body, MethodBase original = null)
        {
            var locals = original.GetMethodBody()?.LocalVariables ?? new List<LocalVariableInfo>();
            var mParams = original.GetParameters();
            var codeInstructions = new List<CodeInstruction>(body.Instructions.Count);

            CodeInstruction ReadInstruction(Instruction ins)
            {
                var cIns = new CodeInstruction(SREOpCodes[ins.OpCode.Value]);

                switch (ins.OpCode.OperandType)
                {
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        cIns.operand = ((MemberReference) ins.Operand).ResolveReflection();
                        break;
                    case OperandType.InlineArg:
                        break;
                    case OperandType.InlineVar:
                    case OperandType.ShortInlineVar:
                        var varDef = (VariableDefinition) ins.Operand;
                        cIns.operand = locals.FirstOrDefault(l => l.LocalIndex == varDef.Index);
                        break;
                    case OperandType.ShortInlineArg:
                        var pDef = (ParameterDefinition) ins.Operand;
                        cIns.operand = mParams.First(p => p.Position == pDef.Index);
                        break;
                    case OperandType.InlineBrTarget:
                    case OperandType.ShortInlineBrTarget:
                        cIns.operand = body.Instructions.IndexOf((Instruction) ins.Operand);
                        break;
                    case OperandType.InlineSwitch:
                        cIns.operand = ((Instruction[]) ins.Operand)
                                       .Select(i => body.Instructions.IndexOf(i)).ToArray();
                        break;
                    default:
                        cIns.operand = ins.Operand;
                        break;
                }

                return cIns;
            }

            // Pass 1: Convert IL to base abstract CodeInstructions
            codeInstructions.AddRange(body.Instructions.Select(ReadInstruction));

            //Pass 2: Resolve CodeInstructions for branch parameters
            foreach (var cIns in codeInstructions)
                switch (cIns.opcode.OperandType)
                {
                    case SRE.OperandType.ShortInlineBrTarget:
                    case SRE.OperandType.InlineBrTarget:
                        cIns.operand = codeInstructions[(int) cIns.operand];
                        break;
                    case SRE.OperandType.InlineSwitch:
                        cIns.operand = ((int[]) cIns.operand).Select(i => codeInstructions[i]).ToArray();
                        break;
                }

            // Pass 3: Attach exception blocks to each code instruction
            foreach (var exception in body.ExceptionHandlers)
            {
                var tryStart = codeInstructions[body.Instructions.IndexOf(exception.TryStart)];
                var tryEnd = codeInstructions[body.Instructions.IndexOf(exception.TryEnd)];
                var handlerStart = codeInstructions[body.Instructions.IndexOf(exception.HandlerStart)];
                var handlerEnd = codeInstructions[body.Instructions.IndexOf(exception.HandlerEnd)];

                tryStart.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
                handlerEnd.blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));

                switch (exception.HandlerType)
                {
                    case ExceptionHandlerType.Catch:
                        handlerStart.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginCatchBlock, exception.CatchType.ResolveReflection()));
                        break;
                    case ExceptionHandlerType.Filter:
                        var filterStart = codeInstructions[body.Instructions.IndexOf(exception.FilterStart)];
                        filterStart.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptFilterBlock));
                        break;
                    case ExceptionHandlerType.Finally:
                        handlerStart.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock));
                        break;
                    case ExceptionHandlerType.Fault:
                        handlerStart.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginFaultBlock));
                        break;
                }
            }

            return codeInstructions;
        }

        /// <summary>
        /// Adds a transpiler method that edits the IL of the given method
        /// </summary>
        /// <param name="transpiler">Transpiler method</param>
        /// <exception cref="NotImplementedException">Currently not implemented</exception>
        public void AddTranspiler(MethodInfo transpiler)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Processes and emits the generated code to the provided target
        /// </summary>
        /// <remarks>
        /// This cleans out all of the method and replaces it with the new definition
        /// </remarks>
        /// <param name="target">Target method to emit code to</param>
        /// <exception cref="NotImplementedException"></exception>
        public void WriteTo(MethodBody target)
        {
            throw new NotImplementedException();
        }
    }

    internal class CodeTranspiler
    {
        private readonly IEnumerable<CodeInstruction> codeInstructions;
        private readonly List<MethodInfo> transpilers = new List<MethodInfo>();

        internal CodeTranspiler(List<ILInstruction> ilInstructions)
        {
            codeInstructions = ilInstructions.Select(ilInstruction => ilInstruction.GetCodeInstruction()).ToList()
                                             .AsEnumerable();
        }

        internal void Add(MethodInfo transpiler)
        {
            transpilers.Add(transpiler);
        }

        internal static object ConvertInstruction(Type type, object instruction,
                                                  out Dictionary<string, object> unassigned)
        {
            var nonExisting = new Dictionary<string, object>();
            var elementTo = AccessTools.MakeDeepCopy(instruction, type, (namePath, trvSrc, trvDest) =>
            {
                var value = trvSrc.GetValue();

                if (trvDest.FieldExists() == false)
                {
                    nonExisting[namePath] = value;
                    return null;
                }

                if (namePath == nameof(CodeInstruction.opcode))
                    return ReplaceShortJumps((OpCode) value);

                return value;
            });
            unassigned = nonExisting;
            return elementTo;
        }

        internal static bool ShouldAddExceptionInfo(object op, int opIndex, List<object> originalInstructions,
                                                    List<object> newInstructions,
                                                    Dictionary<object, Dictionary<string, object>> unassignedValues)
        {
            var originalIndex = originalInstructions.IndexOf(op);
            if (originalIndex == -1)
                return false; // no need, new instruction

            Dictionary<string, object> unassigned = null;
            if (unassignedValues.TryGetValue(op, out unassigned) == false)
                return false; // no need, no unassigned info

            if (unassigned.TryGetValue(nameof(CodeInstruction.blocks), out var blocksObject) == false)
                return false; // no need, no try-catch info
            var blocks = blocksObject as List<ExceptionBlock>;

            var dupCount = newInstructions.Count(instr => instr == op);
            if (dupCount <= 1)
                return true; // ok, no duplicate found

            var isStartBlock = blocks.FirstOrDefault(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
            var isEndBlock = blocks.FirstOrDefault(block => block.blockType == ExceptionBlockType.EndExceptionBlock);

            if (isStartBlock != null && isEndBlock == null)
            {
                var pairInstruction = originalInstructions.Skip(originalIndex + 1).FirstOrDefault(instr =>
                {
                    if (unassignedValues.TryGetValue(instr, out unassigned) == false)
                        return false;
                    if (unassigned.TryGetValue(nameof(CodeInstruction.blocks), out blocksObject) == false)
                        return false;
                    blocks = blocksObject as List<ExceptionBlock>;
                    return blocks.Any();
                });
                if (pairInstruction != null)
                {
                    var pairStart = originalIndex + 1;
                    var pairEnd = pairStart + originalInstructions.Skip(pairStart).ToList().IndexOf(pairInstruction) -
                                  1;
                    var originalBetweenInstructions = originalInstructions
                                                      .GetRange(pairStart, pairEnd - pairStart)
                                                      .Intersect(newInstructions);

                    pairInstruction = newInstructions.Skip(opIndex + 1).FirstOrDefault(instr =>
                    {
                        if (unassignedValues.TryGetValue(instr, out unassigned) == false)
                            return false;
                        if (unassigned.TryGetValue(nameof(CodeInstruction.blocks), out blocksObject) == false)
                            return false;
                        blocks = blocksObject as List<ExceptionBlock>;
                        return blocks.Any();
                    });
                    if (pairInstruction != null)
                    {
                        pairStart = opIndex + 1;
                        pairEnd = pairStart + newInstructions.Skip(opIndex + 1).ToList().IndexOf(pairInstruction) - 1;
                        var newBetweenInstructions = newInstructions.GetRange(pairStart, pairEnd - pairStart);
                        var remaining = originalBetweenInstructions.Except(newBetweenInstructions).ToList();
                        return remaining.Any() == false;
                    }
                }
            }

            if (isStartBlock == null && isEndBlock != null)
            {
                var pairInstruction = originalInstructions.GetRange(0, originalIndex).LastOrDefault(instr =>
                {
                    if (unassignedValues.TryGetValue(instr, out unassigned) == false)
                        return false;
                    if (unassigned.TryGetValue(nameof(CodeInstruction.blocks), out blocksObject) == false)
                        return false;
                    blocks = blocksObject as List<ExceptionBlock>;
                    return blocks.Any();
                });
                if (pairInstruction != null)
                {
                    var pairStart = originalInstructions.GetRange(0, originalIndex).LastIndexOf(pairInstruction);
                    var pairEnd = originalIndex;
                    var originalBetweenInstructions = originalInstructions
                                                      .GetRange(pairStart, pairEnd - pairStart)
                                                      .Intersect(newInstructions);

                    pairInstruction = newInstructions.GetRange(0, opIndex).LastOrDefault(instr =>
                    {
                        if (unassignedValues.TryGetValue(instr, out unassigned) == false)
                            return false;
                        if (unassigned.TryGetValue(nameof(CodeInstruction.blocks), out blocksObject) == false)
                            return false;
                        blocks = blocksObject as List<ExceptionBlock>;
                        return blocks.Any();
                    });
                    if (pairInstruction != null)
                    {
                        pairStart = newInstructions.GetRange(0, opIndex).LastIndexOf(pairInstruction);
                        pairEnd = opIndex;
                        var newBetweenInstructions = newInstructions.GetRange(pairStart, pairEnd - pairStart);
                        var remaining = originalBetweenInstructions.Except(newBetweenInstructions);
                        return remaining.Any() == false;
                    }
                }
            }

            // unclear or unexpected case, ok by default
            return true;
        }

        internal static IEnumerable ConvertInstructionsAndUnassignedValues(
            Type type, IEnumerable enumerable, out Dictionary<object, Dictionary<string, object>> unassignedValues)
        {
            var enumerableAssembly = type.GetGenericTypeDefinition().Assembly;
            var genericListType = enumerableAssembly.GetType(typeof(List<>).FullName);
            var elementType = type.GetGenericArguments()[0];
            var listType =
                enumerableAssembly.GetType(genericListType.MakeGenericType(new Type[] {elementType}).FullName);
            var list = Activator.CreateInstance(listType);
            var listAdd = list.GetType().GetMethod("Add");
            unassignedValues = new Dictionary<object, Dictionary<string, object>>();
            foreach (var op in enumerable)
            {
                var elementTo = ConvertInstruction(elementType, op, out var unassigned);
                unassignedValues.Add(elementTo, unassigned);
                listAdd.Invoke(list, new object[] {elementTo});
                // cannot yield return 'elementTo' here because we have an out parameter in the method
            }

            return list as IEnumerable;
        }

        internal static IEnumerable ConvertToOurInstructions(IEnumerable instructions, Type codeInstructionType,
                                                             List<object> originalInstructions,
                                                             Dictionary<object, Dictionary<string, object>>
                                                                 unassignedValues)
        {
            var newInstructions = instructions.Cast<object>().ToList();

            var index = -1;
            foreach (var op in newInstructions)
            {
                index++;
                var elementTo = AccessTools.MakeDeepCopy(op, codeInstructionType);
                if (unassignedValues.TryGetValue(op, out var fields))
                {
                    var addExceptionInfo =
                        ShouldAddExceptionInfo(op, index, originalInstructions, newInstructions, unassignedValues);

                    var trv = Traverse.Create(elementTo);
                    foreach (var field in fields)
                        if (addExceptionInfo || field.Key != nameof(CodeInstruction.blocks))
                            trv.Field(field.Key).SetValue(field.Value);
                }

                yield return elementTo;
            }
        }

        internal static IEnumerable ConvertToGeneralInstructions(MethodInfo transpiler, IEnumerable enumerable,
                                                                 out Dictionary<object, Dictionary<string, object>>
                                                                     unassignedValues)
        {
            var type = transpiler.GetParameters().Select(p => p.ParameterType).FirstOrDefault(
                t => t.IsGenericType &&
                     t.GetGenericTypeDefinition().Name
                      .StartsWith("IEnumerable", StringComparison.Ordinal));
            return ConvertInstructionsAndUnassignedValues(type, enumerable, out unassignedValues);
        }

        internal static List<object> GetTranspilerCallParameters(ILGenerator generator, MethodInfo transpiler,
                                                                 MethodBase method, IEnumerable instructions)
        {
            var parameter = new List<object>();
            transpiler.GetParameters().Select(param => param.ParameterType).Do(type =>
            {
                if (type.IsAssignableFrom(typeof(ILGenerator)))
                    parameter.Add(generator);
                else if (type.IsAssignableFrom(typeof(MethodBase)))
                    parameter.Add(method);
                else
                    parameter.Add(instructions);
            });
            return parameter;
        }

        internal List<CodeInstruction> GetResult(ILGenerator generator, MethodBase method)
        {
            IEnumerable instructions = codeInstructions;
            transpilers.ForEach(transpiler =>
            {
                // before calling some transpiler, convert the input to 'their' CodeInstruction type
                // also remember any unassignable values that otherwise would be lost
                instructions = ConvertToGeneralInstructions(transpiler, instructions, out var unassignedValues);

                // remember the order of the original input (for detection of dupped code instructions)
                var originalInstructions = new List<object>();
                originalInstructions.AddRange(instructions.Cast<object>());

                // call the transpiler
                var parameter = GetTranspilerCallParameters(generator, transpiler, method, instructions);
                instructions = transpiler.Invoke(null, parameter.ToArray()) as IEnumerable;

                // convert result back to 'our' CodeInstruction and re-assign otherwise lost fields
                instructions = ConvertToOurInstructions(instructions, typeof(CodeInstruction), originalInstructions,
                                                        unassignedValues);
            });
            return instructions.Cast<CodeInstruction>().ToList();
        }

        //

        private static readonly Dictionary<OpCode, OpCode> allJumpCodes = new Dictionary<OpCode, OpCode>
        {
            {OpCodes.Beq_S, OpCodes.Beq},
            {OpCodes.Bge_S, OpCodes.Bge},
            {OpCodes.Bge_Un_S, OpCodes.Bge_Un},
            {OpCodes.Bgt_S, OpCodes.Bgt},
            {OpCodes.Bgt_Un_S, OpCodes.Bgt_Un},
            {OpCodes.Ble_S, OpCodes.Ble},
            {OpCodes.Ble_Un_S, OpCodes.Ble_Un},
            {OpCodes.Blt_S, OpCodes.Blt},
            {OpCodes.Blt_Un_S, OpCodes.Blt_Un},
            {OpCodes.Bne_Un_S, OpCodes.Bne_Un},
            {OpCodes.Brfalse_S, OpCodes.Brfalse},
            {OpCodes.Brtrue_S, OpCodes.Brtrue},
            {OpCodes.Br_S, OpCodes.Br},
            {OpCodes.Leave_S, OpCodes.Leave}
        };

        private static OpCode ReplaceShortJumps(OpCode opcode)
        {
            foreach (var pair in allJumpCodes)
                if (opcode == pair.Key)
                    return pair.Value;
            return opcode;
        }
    }
}