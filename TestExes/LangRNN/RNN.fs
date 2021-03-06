﻿namespace LangRNN

open Basics
open ArrayNDNS
open SymTensor
open SymTensor.Compiler.Cuda

open Models


/// A layer of recurrent neurons with an output layer on top.
module RecurrentLayer =

    /// Neural layer hyper-parameters.
    type HyperPars = {
        /// number of inputs
        NInput:                     SizeSpecT
        /// number of recurrent units
        NRecurrent:                 SizeSpecT
        /// number of output units
        NOutput:                    SizeSpecT
        /// recurrent transfer (activation) function
        RecurrentActivationFunc:    ActivationFunc
        /// output transfer (activation) function
        OutputActivationFunc:       ActivationFunc
        /// if true the input is an integer specifying which element
        /// of an equivalent one-hot input would be active
        OneHotIndexInput:           bool
        /// input weights trainable
        InputWeightsTrainable:      bool
        /// recurrent weights trainable
        RecurrentWeightsTrainable:  bool
        /// output weights trainable
        OutputWeightsTrainable:     bool
        /// recurrent bias trainable
        RecurrentBiasTrainable:     bool
        /// output bias trainable
        OutputBiasTrainable:        bool
    }

    /// default hyper-parameters
    let defaultHyperPars = {
        NInput                      = SizeSpec.fix 0L
        NRecurrent                  = SizeSpec.fix 0L
        NOutput                     = SizeSpec.fix 0L
        RecurrentActivationFunc     = Tanh
        OutputActivationFunc        = SoftMax
        OneHotIndexInput            = false
        InputWeightsTrainable       = true
        RecurrentWeightsTrainable   = true
        OutputWeightsTrainable      = true
        RecurrentBiasTrainable      = true
        OutputBiasTrainable         = true
    }

    /// Neural layer parameters.
    type Pars = {
        /// expression for input-to-recurrent weights 
        InputWeights:      ExprT 
        /// expression for recurrent-to-recurrent weights
        RecurrentWeights:  ExprT 
        /// expression for recurrent-to-output weights
        OutputWeights:     ExprT
        /// expression for the recurrent bias
        RecurrentBias:     ExprT 
        /// expression for the output bias
        OutputBias:        ExprT 
        /// hyper-parameters
        HyperPars:         HyperPars
    }

    let internal initWeights seed (shp: int64 list) : ArrayNDHostT<'T> = 
        let fanIn = float shp.[1] 
        let r = 1.0 / sqrt fanIn       
        (System.Random seed).SeqDouble(-1.0, 1.0)
        |> Seq.map conv<'T>
        |> ArrayNDHost.ofSeqWithShape shp
        
    let internal initBias seed (shp: int64 list) : ArrayNDHostT<'T> =
        Seq.initInfinite (fun _ -> conv<'T> 0)
        |> ArrayNDHost.ofSeqWithShape shp

    /// Creates the parameters.
    let pars (mb: ModelBuilder<_>) hp = {
        InputWeights     = mb.Param ("InputWeights",     [hp.NRecurrent; hp.NInput],     initWeights)
        RecurrentWeights = mb.Param ("RecurrentWeights", [hp.NRecurrent; hp.NRecurrent], initWeights)
        OutputWeights    = mb.Param ("OutputWeights",    [hp.NOutput;    hp.NRecurrent], initWeights)
        RecurrentBias    = mb.Param ("RecurrentBias",    [hp.NRecurrent],                initBias)
        OutputBias       = mb.Param ("OutputBias",       [hp.NOutput],                   initBias)
        HyperPars        = hp
    }

    /// Predicts an output sequence by running the RNN over the input sequence.
    /// Argument shapes: initial[smpl, recUnit],  input[smpl, step, inUnit].
    /// Output shapes:     final[smpl, recUnit], output[smpl, step, outUnit].
    let pred (pars: Pars) (initial: ExprT) (input: ExprT) =
        // inputWeights     [recUnit, inUnit]
        // recurrentWeights [recUnit, recUnit]
        // outputWeights    [outUnit, recUnit]
        // recurrentBias    [recUnit]
        // outputBias       [recUnit]
        // input            [smpl, step]           - if OneHotIndexInput=true 
        // input            [smpl, step, inUnit]   - if OneHotIndexInput=false
        // initial          [smpl, recUnit]
        // state            [smpl, step, recUnit]
        // output           [smpl, step, outUnit]

        // gate gradients
        let inputWeights     = Util.gradGate pars.HyperPars.InputWeightsTrainable     pars.InputWeights
        let recurrentWeights = Util.gradGate pars.HyperPars.RecurrentWeightsTrainable pars.RecurrentWeights
        let outputWeights    = Util.gradGate pars.HyperPars.OutputWeightsTrainable    pars.OutputWeights
        let recurrentBias    = Util.gradGate pars.HyperPars.RecurrentBiasTrainable    pars.RecurrentBias
        let outputBias       = Util.gradGate pars.HyperPars.OutputBiasTrainable       pars.OutputBias

        let nBatch = input.Shape.[0]
        let nSteps = input.Shape.[1]
        let nRecurrent = pars.HyperPars.NRecurrent

        // build loop
        let inputSlice = 
            if pars.HyperPars.OneHotIndexInput then
                Expr.var<int> "InputSlice"  [nBatch]
            else
                Expr.var<single> "InputSlice"  [nBatch; pars.HyperPars.NInput]
        let prevState = 
            Expr.var<single> "PrevState"   [nBatch; pars.HyperPars.NRecurrent]
        let state =
            let inpAct = // [smpl, recUnit]
                if pars.HyperPars.OneHotIndexInput then
                    //                      bcRecUnit    [smpl*, recUnit]
                    // inputSlice [smpl] => bcInputSlice [smpl, recUnit*]
                    let bcRecUnit = Expr.arange<int> nRecurrent |> Expr.padLeft |> Expr.broadcast [nBatch; nRecurrent]
                    let bcInputSlice = inputSlice |> Expr.padRight |> Expr.broadcast [nBatch; nRecurrent]
                    //let bcRecUnit = bcRecUnit |> Expr.print "bcRecUnit"
                    //let bcInputSlice = bcInputSlice |> Expr.print "bcInputSlice"
                    inputWeights |> Expr.gather [Some bcRecUnit; Some bcInputSlice]
                else
                    inputSlice .* inputWeights.T
            //let inpAct = inpAct |> Expr.print "inpAct"
            let recAct = prevState .* recurrentWeights.T
            inpAct + recAct //+ recurrentBias
            |> ActivationFunc.apply pars.HyperPars.RecurrentActivationFunc
        let output =
            state .* outputWeights.T //+ outputBias
            |> ActivationFunc.apply pars.HyperPars.OutputActivationFunc
        let chState, chOutput = "State", "Output"
        let loopSpec = {
            Expr.Length = nSteps
            Expr.Vars = Map [Expr.extractVar inputSlice, Expr.SequenceArgSlice {ArgIdx=0; SliceDim=1}
                             Expr.extractVar prevState, 
                                    Expr.PreviousChannel {Channel=chState; Delay=SizeSpec.fix 1L; InitialArg=1}]
            Expr.Channels = Map [chState,  {LoopValueT.Expr=state;  LoopValueT.SliceDim=1}
                                 chOutput, {LoopValueT.Expr=output; LoopValueT.SliceDim=1}]    
        }

        let initial = initial |> Expr.reshape [nBatch; SizeSpec.fix 1L; nRecurrent]
        let states  = Expr.loop loopSpec chState  [input; initial]
        let outputs = Expr.loop loopSpec chOutput [input; initial]
        let final = states.[*, nSteps-1L, *]

        final, outputs


