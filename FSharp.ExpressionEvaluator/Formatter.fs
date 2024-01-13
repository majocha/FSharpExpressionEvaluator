namespace FSharp.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger.ComponentInterfaces

type Formatter() =
    interface IDkmClrFormatter with

        member _.GetTypeName(inspectionContext, clrType, customTypeInfo, formatSpecifiers) =
            inspectionContext.GetTypeName(clrType, customTypeInfo, formatSpecifiers) + " ❤️!"

        member _.GetUnderlyingString(clrValue, inspectionContext) =
            clrValue.GetUnderlyingString(inspectionContext) + " ❤️!"

        member _.GetValueString(clrValue, inspectionContext, formatSpecifiers) =
            clrValue.GetValueString(inspectionContext, formatSpecifiers) + " ❤️!"

        member _.HasUnderlyingString(clrValue, inspectionContext) =
            clrValue.HasUnderlyingString inspectionContext
