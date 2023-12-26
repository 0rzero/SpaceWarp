﻿using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using KSP.Game;
using KSP.Game.Flow;

namespace SpaceWarp.Patching;

[HarmonyPatch]
internal static class SequentialFlowLoadersPatcher
{
    internal const int FlowMethodStartgame = 0;
    internal const int FlowMethodPrivateloadcommon = 1;
    internal const int FlowMethodPrivatesavecommon = 2;

    private static SequentialFlowAdditions[] _sequentialFlowAdditions =
    {
        new(typeof(GameManager).GetMethod("StartGame")), // Must be index FLOW_METHOD_STARTGAME
        new(AccessTools.Method(
            "KSP.Game.SaveLoadManager:PrivateLoadCommon")), // Must be index FLOW_METHOD_PRIVATELOADCOMMON
        new(AccessTools.Method(
            "KSP.Game.SaveLoadManager:PrivateSaveCommon")), // Must be index FLOW_METHOD_PRIVATESAVECOMMON
    };

    internal static void AddConstructor(string after, Type flowAction, int methodIndex)
    {
        _sequentialFlowAdditions[methodIndex].AddConstructor(after, flowAction);
    }

    internal static void AddFlowAction(string after, FlowAction flowAction, int methodIndex)
    {
        _sequentialFlowAdditions[methodIndex].AddAction(after, flowAction);
    }

    public static SequentialFlow Apply(SequentialFlow flow, object[] methodArguments, int methodIndex)
    {
        _sequentialFlowAdditions[methodIndex].ApplyTo(flow, methodArguments);
        return flow;
    }

    private static IEnumerable<CodeInstruction> TranspileSequentialFlowBuilderMethod(
        IEnumerable<CodeInstruction> instructions,
        int methodIndex
    )
    {
        var startFlow = typeof(SequentialFlow).GetMethod("StartFlow");

        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Callvirt && startFlow == (MethodInfo)instruction.operand)
            {
                // Call to StartFlow found!
                // Before it occurs, insert any extra flow actions.

                // Get list of relevant arguments to pass to FlowAction constructors.
                // `parameterCount` has a `+ 1` because `GetParameters()` doesn't include the instance parameter (this).
                var parameters = _sequentialFlowAdditions[methodIndex].method.GetParameters()
                    .Where(parameter => !parameter.ParameterType.IsValueType).ToArray();
                var parameterCount = parameters.Length + 1;

                // Creation of argument `methodArguments` to `Apply()`:

                // Construct an array to hold the arguments.
                yield return CodeGenerationUtilities.PushIntInstruction(parameterCount);
                yield return new CodeInstruction(OpCodes.Newarr, typeof(object));

                // Assign `this` to element 0.
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Stelem_Ref);

                // Assign the other arguments.
                for (int i = 1; i < parameterCount; i++)
                {
                    var parameter = parameters[i - 1];
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return CodeGenerationUtilities.PushIntInstruction(i);
                    yield return new CodeInstruction(OpCodes.Ldarg_S, (byte)(parameter.Position + 1));
                    yield return new CodeInstruction(OpCodes.Stelem_Ref);
                }

                // Creation of argument `methodIndex` to `Apply()`:
                yield return CodeGenerationUtilities.PushIntInstruction(methodIndex);

                // Call `Apply()`.
                // `flow` is already on the stack and does not need to be created.
                yield return new CodeInstruction(
                    OpCodes.Call,
                    typeof(SequentialFlowLoadersPatcher).GetMethod("Apply")
                );
            }

            // Copy everything else.
            yield return instruction;
        }
    }

    [HarmonyPatch(typeof(GameManager))]
    [HarmonyPatch("StartGame")]
    [HarmonyPrefix]
    public static void PrefixGameManagerStartGame(GameManager __instance)
    {
        _sequentialFlowAdditions[FlowMethodStartgame].ApplyTo(
            __instance.LoadingFlow,
            new object[] { __instance }
        );
    }

    [HarmonyPatch(typeof(SaveLoadManager))]
    [HarmonyPatch("PrivateLoadCommon")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> TranspileSaveLoadManagerPrivateLoadCommon(
        IEnumerable<CodeInstruction> instructions)
    {
        return TranspileSequentialFlowBuilderMethod(instructions, FlowMethodPrivateloadcommon);
    }

    [HarmonyPatch(typeof(SaveLoadManager))]
    [HarmonyPatch("PrivateSaveCommon")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> TranspileSaveLoadManagerPrivateSaveCommon(
        IEnumerable<CodeInstruction> instructions)
    {
        return TranspileSequentialFlowBuilderMethod(instructions, FlowMethodPrivatesavecommon);
    }

    private class SequentialFlowAdditions
    {
        private readonly HashSet<Type> availableTypes;
        private readonly List<KeyValuePair<string, object>> insertAfter = new();
        internal readonly MethodInfo method;

        internal SequentialFlowAdditions(MethodInfo method)
        {
            availableTypes =
                new HashSet<Type>(method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Where(type => !type.IsValueType)) { method.DeclaringType };
            this.method = method;
        }

        internal void AddConstructor(string after, Type flowAction)
        {
            // Determine the correct constructor to use.
            var constructor = flowAction.GetConstructors()
                .OrderByDescending(constructor => constructor.GetParameters().Length).Where(constructor =>
                {
                    HashSet<Type> seen = new();

                    foreach (var parameter in constructor.GetParameters())
                    {
                        if (!availableTypes.Contains(parameter.ParameterType))
                            return false;

                        if (!seen.Add(parameter.GetType()))
                            return false;
                    }

                    return true;
                }).FirstOrDefault() ?? throw new InvalidOperationException(
                $"Flow action type {flowAction.Name} does not have a public constructor that has parameters compatible with {method.DeclaringType.Name}.{method.Name}");

            insertAfter.Add(new KeyValuePair<string, object>(after, constructor));
        }

        internal void AddAction(string after, FlowAction action)
        {
            insertAfter.Add(new KeyValuePair<string, object>(after, action));
        }

        private static FlowAction Construct(ConstructorInfo constructor, object[] methodArguments)
        {
            // Figure out which type of object goes where in the arguments list.
            var parameterIndices = constructor.GetParameters().ToDictionary(
                parameter => parameter.ParameterType,
                parameter => parameter.Position
            );

            // Create and populate the arguments list
            var arguments = new object[constructor.GetParameters().Length];

            foreach (var obj in methodArguments)
            {
                if (parameterIndices.TryGetValue(obj.GetType(), out var index))
                {
                    arguments[index] = obj;
                }
            }

            return (FlowAction)constructor.Invoke(arguments);
        }

        private void AddActionsAfter(string name, List<FlowAction> actions, bool[] added, object[] methodArguments)
        {
            for (int i = 0; i < added.Length; i++)
            {
                // Action has already been added.
                if (added[i])
                    continue;

                if (insertAfter[i].Key != name) continue;

                added[i] = true;

                var action = insertAfter[i].Value switch
                {
                    ConstructorInfo constructor => Construct(constructor, methodArguments),
                    FlowAction flowAction => flowAction,
                    _ => null
                };

                actions.Add(action);

                // There could be actions set to run after the newly inserted action,
                // so this needs to be called again.
                AddActionsAfter(action.Name, actions, added, methodArguments);
            }
        }

        internal void ApplyTo(SequentialFlow flow, object[] methodArguments)
        {
            // Uncomment this section to log a list of FlowActions in the flow by default,
            // for use in the documentation of the methods in Loading.SaveLoad.
            /*
            StringBuilder sb = new("Flow items for ");
            sb.Append(method.Name);
            sb.Append(":\n");
            foreach (var action in flow._flowActions)
            {
                sb.Append("<item><c>\"");
                sb.Append(action.Name);
                sb.Append("\"</c></item>\n");
            }
            Logger.CreateLogSource("SequentialFlow Logger").Log(LogLevel.Info, sb.ToString());
            */

            // New list of actions
            List<FlowAction> actions = new();

            // Has each action been added already?
            // Used to prevent duplicates, and warn about unused actions.
            var added = new bool[insertAfter.Count()];

            // `null` is sued to signify the start of the action list.
            AddActionsAfter(null, actions, added, methodArguments);

            // Add actions from the existing flow, and any new ones in the right place.
            foreach (var action in flow._flowActions)
            {
                actions.Add(action);
                AddActionsAfter(action.Name, actions, added, methodArguments);
            }

            // Replace the flow's list with our new one.
            flow._flowActions.Clear();
            flow._flowActions.AddRange(actions);

            // Warn about any actions that were not used.
            for (int i = 0; i < added.Length; i++)
            {
                if (added[i]) continue;

                if (insertAfter[i].Value is ConstructorInfo constructor)
                {
                    var actionType = constructor.DeclaringType;
                    var logger = Logger.CreateLogSource($"{actionType.Assembly.GetName().Name}/{actionType.Name}");
                    logger.LogWarning(
                        $"Flow action {actionType.Name} was set to be inserted after \"{insertAfter[i].Key}\" in {method.Name}, however that action does not exist in that flow");
                }
                else if (insertAfter[i].Value is FlowAction action)
                {
                    var logger = Logger.CreateLogSource("SequentialFlow");
                    logger.LogWarning(
                        $"Flow action \"{action.Name}\" was set to be inserted after \"{insertAfter[i].Key}\" in {method.Name}, however that action does not exist in that flow");
                }
            }
        }
    }
}