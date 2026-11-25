using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ProbablyCoop;

// Resolves fixed game signatures from the audited manifests through the game's
// scoped reflection API. Parameter types outside the game are explicit.
internal sealed class NativeGameMethod
{
    private readonly Module module;
    private readonly Dictionary<string, Type> gameTypes = new Dictionary<string, Type>(StringComparer.Ordinal);

    internal NativeGameMethod(Module module) => this.module = module;

    internal MethodBase Resolve(string signature)
    {
        int typeStart = signature.IndexOf(' ') + 1;
        int typeEnd = signature.IndexOf("::", typeStart, StringComparison.Ordinal);
        int arguments = signature.IndexOf('(', typeEnd + 2);
        var type = GameType(signature.Substring(typeStart, typeEnd - typeStart));
        var name = signature.Substring(typeEnd + 2, arguments - typeEnd - 2);
        var parameters = Parameters(signature.Substring(arguments + 1, signature.Length - arguments - 2));
        var method = name == ".ctor" && type == typeof(PlayerStore) && parameters.Length == 0
            ? AccessTools.Constructor(typeof(PlayerStore), Type.EmptyTypes)
            : ModReflect.Target(type, name, parameters);
        if (method == null || method.DeclaringType != type || Describe(method) != signature)
            throw new InvalidDataException("Audited game method changed: " + signature);
        return method;
    }

    private Type[] Parameters(string arguments)
    {
        if (arguments.Length == 0) return Type.EmptyTypes;
        var parameters = new List<Type>();
        int depth = 0, start = 0;
        for (int i = 0; i < arguments.Length; ++i)
        {
            char character = arguments[i];
            if (character == '<') ++depth;
            else if (character == '>') --depth;
            else if (character == ',' && depth == 0)
            {
                parameters.Add(ParameterType(arguments.Substring(start, i - start)));
                start = i + 1;
            }
        }
        parameters.Add(ParameterType(arguments.Substring(start)));
        return parameters.ToArray();
    }

    private Type ParameterType(string name) => name switch
    {
        "System.Action" => typeof(Action),
        "System.Boolean" => typeof(bool),
        "System.Byte" => typeof(byte),
        "System.Byte[]" => typeof(byte[]),
        "System.Int32" => typeof(int),
        "System.Int32[]" => typeof(int[]),
        "System.Int64" => typeof(long),
        "System.Single" => typeof(float),
        "System.String" => typeof(string),
        "System.Collections.Generic.Dictionary`2<System.String,TagState>" => typeof(Dictionary<string, TagState>),
        "System.Collections.Generic.List`1<CombatAbilityEffect>" => typeof(List<CombatAbilityEffect>),
        "System.Collections.Generic.List`1<GameItem>" => typeof(List<GameItem>),
        "System.Nullable`1<UnityEngine.Color>" => typeof(Color?),
        "TMPro.HorizontalAlignmentOptions" => typeof(HorizontalAlignmentOptions),
        "UnityEngine.Color" => typeof(Color),
        "UnityEngine.Color32" => typeof(Color32),
        "UnityEngine.Sprite" => typeof(Sprite),
        "UnityEngine.Vector2" => typeof(Vector2),
        _ => GameType(name)
    };

    private Type GameType(string name)
    {
        if (gameTypes.TryGetValue(name, out var type)) return type;
        type = ModReflect.FindType(name.Replace('/', '+'));
        if (type?.Module != module) throw new InvalidDataException("Unknown audited game type: " + name);
        gameTypes.Add(name, type);
        return type;
    }

    internal static string Describe(MethodBase method) => TypeName((method as MethodInfo)?.ReturnType ?? typeof(void)) +
        " " + TypeName(method.DeclaringType) + "::" + method.Name + "(" +
        string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) + ")";

    private static string TypeName(Type type)
    {
        if (type.IsByRef) return TypeName(type.GetElementType()) + "&";
        if (type.IsArray) return TypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericType) return type.GetGenericTypeDefinition().FullName.Replace('+', '/') + "<" +
            string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        return type.FullName?.Replace('+', '/') ?? type.Name;
    }
}
