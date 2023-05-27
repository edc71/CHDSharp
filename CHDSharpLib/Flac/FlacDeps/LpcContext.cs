using System;
using System.Collections.Generic;
using CUETools.Codecs;

namespace CHDReaderTest.Flac.FlacDeps
{
    unsafe public class LpcSubframeInfo
    {
        public LpcSubframeInfo()
        {
            autocorr_section_values = new double[lpc.MAX_LPC_SECTIONS, lpc.MAX_LPC_ORDER + 1];
            autocorr_section_orders = new int[lpc.MAX_LPC_SECTIONS];
        }

        // public LpcContext[] lpc_ctx;
        public double[,] autocorr_section_values;
        public int[] autocorr_section_orders;
        //public int obits;

        public void Reset()
        {
            for (int sec = 0; sec < autocorr_section_orders.Length; sec++)
                autocorr_section_orders[sec] = 0;
        }
    }

    unsafe public struct LpcWindowSection
    {
        public enum SectionType
        {
            Zero,
            One,
            OneLarge,
            Data,
            OneGlue,
            Glue
        };
        public int m_start;
        public int m_end;
        public SectionType m_type;
        public int m_id;
        public LpcWindowSection(int end)
        {
            m_id = -1;
            m_start = 0;
            m_end = end;
            m_type = SectionType.Data;
        }
        public void setData(int start, int end)
        {
            m_id = -1;
            m_start = start;
            m_end = end;
            m_type = SectionType.Data;
        }
        public void setZero(int start, int end)
        {
            m_id = -1;
            m_start = start;
            m_end = end;
            m_type = SectionType.Zero;
        }
    }

    /// <summary>
    /// Context for LPC coefficients calculation and order estimation
    /// </summary>
    unsafe public class LpcContext
    {
        public LpcContext()
        {
            coefs = new int[lpc.MAX_LPC_ORDER];
            reflection_coeffs = new double[lpc.MAX_LPC_ORDER];
            prediction_error = new double[lpc.MAX_LPC_ORDER];
            autocorr_values = new double[lpc.MAX_LPC_ORDER + 1];
            best_orders = new int[lpc.MAX_LPC_ORDER];
            done_lpcs = new uint[lpc.MAX_LPC_PRECISIONS];
        }

        public double[] autocorr_values;
        double[] reflection_coeffs;
        public double[] prediction_error;
        public int[] best_orders;
        public int[] coefs;
        public int shift;

        public uint[] done_lpcs;
    }
}
