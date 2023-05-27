using System;

namespace CHDReaderTest.Flac.FlacDeps
{
    public class lpc
    {
        public const int MAX_LPC_ORDER = 32;
        public const int MAX_LPC_WINDOWS = 16;
        public const int MAX_LPC_PRECISIONS = 4;
        public const int MAX_LPC_SECTIONS = 128;



        /**
		 * Calculates autocorrelation data from audio samples
		 * A window function is applied before calculation.
		 */
        static public unsafe void
            compute_autocorr(/*const*/ int* data, float* window, int len, int min, int lag, double* autoc)
        {
            double* data1 = stackalloc double[len];
            int i;

            for (i = 0; i < len; i++)
                data1[i] = data[i] * window[i];

            for (i = min; i <= lag; ++i)
            {
                double temp = 0;
                double temp2 = 0;
                double* pdata = data1;
                double* finish = data1 + len - 1 - i;

                while (pdata < finish)
                {
                    temp += pdata[i] * *pdata++;
                    temp2 += pdata[i] * *pdata++;
                }
                if (pdata <= finish)
                    temp += pdata[i] * *pdata++;

                autoc[i] += temp + temp2;
            }
        }

        static public unsafe void
            compute_autocorr_windowless(/*const*/ int* data, int len, int min, int lag, double* autoc)
        {
            // if databits*2 + log2(len) <= 64
            for (int i = min; i <= lag; ++i)
            {
                long temp = 0;
                long temp2 = 0;
                int* pdata = data;
                int* finish = data + len - i - 1;
                while (pdata < finish)
                {
                    temp += (long)pdata[i] * *pdata++;
                    temp2 += (long)pdata[i] * *pdata++;
                }
                if (pdata <= finish)
                    temp += (long)pdata[i] * *pdata++;
                autoc[i] += temp + temp2;
            }
        }

        static public unsafe void
            compute_autocorr_windowless_large(/*const*/ int* data, int len, int min, int lag, double* autoc)
        {
            for (int i = min; i <= lag; ++i)
            {
                double temp = 0;
                double temp2 = 0;
                int* pdata = data;
                int* finish = data + len - i - 1;
                while (pdata < finish)
                {
                    temp += (long)pdata[i] * *pdata++;
                    temp2 += (long)pdata[i] * *pdata++;
                }
                if (pdata <= finish)
                    temp += (long)pdata[i] * *pdata++;
                autoc[i] += temp + temp2;
            }
        }

        static public unsafe void
            compute_autocorr_glue(/*const*/ int* data, float* window, int offs, int offs1, int min, int lag, double* autoc)
        {
            double* data1 = stackalloc double[lag + lag];
            for (int i = -lag; i < lag; i++)
                data1[i + lag] = offs + i >= 0 && offs + i < offs1 ? data[offs + i] * window[offs + i] : 0;
            for (int i = min; i <= lag; ++i)
            {
                double temp = 0;
                double* pdata = data1 + lag - i;
                double* finish = data1 + lag;
                while (pdata < finish)
                    temp += pdata[i] * *pdata++;
                autoc[i] += temp;
            }
        }

        static public unsafe void
            compute_autocorr_glue(/*const*/ int* data, int min, int lag, double* autoc)
        {
            for (int i = min; i <= lag; ++i)
            {
                long temp = 0;
                int* pdata = data - i;
                int* finish = data;
                while (pdata < finish)
                    temp += (long)pdata[i] * *pdata++;
                autoc[i] += temp;
            }
        }

        /**
		 * Levinson-Durbin recursion.
		 * Produces LPC coefficients from autocorrelation data.
		 */
        public static unsafe void
        compute_lpc_coefs(uint max_order, double* reff, float* lpc/*[][MAX_LPC_ORDER]*/)
        {
            double* lpc_tmp = stackalloc double[MAX_LPC_ORDER];

            if (max_order > MAX_LPC_ORDER)
                throw new Exception("weird");

            for (int i = 0; i < max_order; i++)
                lpc_tmp[i] = 0;

            for (int i = 0; i < max_order; i++)
            {
                double r = reff[i];
                int i2 = i >> 1;
                lpc_tmp[i] = r;
                for (int j = 0; j < i2; j++)
                {
                    double tmp = lpc_tmp[j];
                    lpc_tmp[j] += r * lpc_tmp[i - 1 - j];
                    lpc_tmp[i - 1 - j] += r * tmp;
                }

                if (0 != (i & 1))
                    lpc_tmp[i2] += lpc_tmp[i2] * r;

                for (int j = 0; j <= i; j++)
                    lpc[i * MAX_LPC_ORDER + j] = (float)-lpc_tmp[j];
            }
        }

        public static unsafe void
        compute_schur_reflection(/*const*/ double* autoc, uint max_order,
                              double* reff/*[][MAX_LPC_ORDER]*/, double* err)
        {
            double* gen0 = stackalloc double[MAX_LPC_ORDER];
            double* gen1 = stackalloc double[MAX_LPC_ORDER];

            // Schur recursion
            for (uint i = 0; i < max_order; i++)
                gen0[i] = gen1[i] = autoc[i + 1];

            double error = autoc[0];
            reff[0] = -gen1[0] / error;
            error += gen1[0] * reff[0];
            err[0] = error;
            for (uint i = 1; i < max_order; i++)
            {
                for (uint j = 0; j < max_order - i; j++)
                {
                    gen1[j] = gen1[j + 1] + reff[i - 1] * gen0[j];
                    gen0[j] = gen1[j + 1] * reff[i - 1] + gen0[j];
                }
                reff[i] = -gen1[0] / error;
                error += gen1[0] * reff[i];
                err[i] = error;
            }
        }


        public static unsafe void
        decode_residual(int* res, int* smp, int n, int order,
            int* coefs, int shift)
        {
            for (int i = 0; i < order; i++)
                smp[i] = res[i];

            int* s = smp;
            int* r = res + order;
            int c0 = coefs[0];
            int c1 = coefs[1];
            switch (order)
            {
                case 1:
                    for (int i = n - order; i > 0; i--)
                    {
                        int pred = c0 * *s++;
                        *s = *r++ + (pred >> shift);
                    }
                    break;
                case 2:
                    for (int i = n - order; i > 0; i--)
                    {
                        int pred = c1 * *s++ + c0 * *s++;
                        *s-- = *r++ + (pred >> shift);
                    }
                    break;
                case 3:
                    for (int i = n - order; i > 0; i--)
                    {
                        int* co = coefs + order - 1;
                        int pred =
                            *co-- * *s++ +
                            c1 * *s++ + c0 * *s++;
                        *s = *r++ + (pred >> shift);
                        s -= 2;
                    }
                    break;
                case 4:
                    for (int i = n - order; i > 0; i--)
                    {
                        int* co = coefs + order - 1;
                        int pred =
                            *co-- * *s++ + *co-- * *s++ +
                            c1 * *s++ + c0 * *s++;
                        *s = *r++ + (pred >> shift);
                        s -= 3;
                    }
                    break;
                case 5:
                    for (int i = n - order; i > 0; i--)
                    {
                        int* co = coefs + order - 1;
                        int pred =
                            *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            c1 * *s++ + c0 * *s++;
                        *s = *r++ + (pred >> shift);
                        s -= 4;
                    }
                    break;
                case 6:
                    for (int i = n - order; i > 0; i--)
                    {
                        int* co = coefs + order - 1;
                        int pred =
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            c1 * *s++ + c0 * *s++;
                        *s = *r++ + (pred >> shift);
                        s -= 5;
                    }
                    break;
                case 7:
                    for (int i = n - order; i > 0; i--)
                    {
                        int* co = coefs + order - 1;
                        int pred =
                            *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            c1 * *s++ + c0 * *s++;
                        *s = *r++ + (pred >> shift);
                        s -= 6;
                    }
                    break;
                case 8:
                    for (int i = n - order; i > 0; i--)
                    {
                        int* co = coefs + order - 1;
                        int pred =
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            c1 * *s++ + c0 * *s++;
                        *s = *r++ + (pred >> shift);
                        s -= 7;
                    }
                    break;
                case 9:
                    for (int i = n - order; i > 0; i--)
                    {
                        int* co = coefs + order - 1;
                        int pred =
                            *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            c1 * *s++ + c0 * *s++;
                        *s = *r++ + (pred >> shift);
                        s -= 8;
                    }
                    break;
                case 10:
                    for (int i = n - order; i > 0; i--)
                    {
                        int* co = coefs + order - 1;
                        int pred =
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            c1 * *s++ + c0 * *s++;
                        *s = *r++ + (pred >> shift);
                        s -= 9;
                    }
                    break;
                case 11:
                    for (int i = n - order; i > 0; i--)
                    {
                        int* co = coefs + order - 1;
                        int pred =
                            *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            c1 * *s++ + c0 * *s++;
                        *s = *r++ + (pred >> shift);
                        s -= 10;
                    }
                    break;
                case 12:
                    for (int i = n - order; i > 0; i--)
                    {
                        int* co = coefs + order - 1;
                        int pred =
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            *co-- * *s++ + *co-- * *s++ +
                            c1 * *s++ + c0 * *s++;
                        *s = *r++ + (pred >> shift);
                        s -= 11;
                    }
                    break;
                default:
                    for (int i = order; i < n; i++)
                    {
                        s = smp + i - order;
                        int pred = 0;
                        int* co = coefs + order - 1;
                        int* c7 = coefs + 7;
                        while (co > c7)
                            pred += *co-- * *s++;
                        pred += coefs[7] * *s++;
                        pred += coefs[6] * *s++;
                        pred += coefs[5] * *s++;
                        pred += coefs[4] * *s++;
                        pred += coefs[3] * *s++;
                        pred += coefs[2] * *s++;
                        pred += c1 * *s++;
                        pred += c0 * *s++;
                        *s = *r++ + (pred >> shift);
                    }
                    break;
            }
        }
        public static unsafe void
        decode_residual_long(int* res, int* smp, int n, int order,
            int* coefs, int shift)
        {
            for (int i = 0; i < order; i++)
                smp[i] = res[i];

            int* s = smp;
            int* r = res + order;
            int c0 = coefs[0];
            int c1 = coefs[1];
            switch (order)
            {
                case 1:
                    for (int i = n - order; i > 0; i--)
                    {
                        long pred = c0 * (long)*s++;
                        *s = *r++ + (int)(pred >> shift);
                    }
                    break;
                case 2:
                    for (int i = n - order; i > 0; i--)
                    {
                        long pred = c1 * (long)*s++;
                        pred += c0 * (long)*s++;
                        *s-- = *r++ + (int)(pred >> shift);
                    }
                    break;
                case 3:
                    for (int i = n - order; i > 0; i--)
                    {
                        long pred = coefs[2] * (long)*s++;
                        pred += c1 * (long)*s++;
                        pred += c0 * (long)*s++;
                        *s = *r++ + (int)(pred >> shift);
                        s -= 2;
                    }
                    break;
                case 4:
                    for (int i = n - order; i > 0; i--)
                    {
                        long pred = coefs[3] * (long)*s++;
                        pred += coefs[2] * (long)*s++;
                        pred += c1 * (long)*s++;
                        pred += c0 * (long)*s++;
                        *s = *r++ + (int)(pred >> shift);
                        s -= 3;
                    }
                    break;
                case 5:
                    for (int i = n - order; i > 0; i--)
                    {
                        long pred = coefs[4] * (long)*s++;
                        pred += coefs[3] * (long)*s++;
                        pred += coefs[2] * (long)*s++;
                        pred += c1 * (long)*s++;
                        pred += c0 * (long)*s++;
                        *s = *r++ + (int)(pred >> shift);
                        s -= 4;
                    }
                    break;
                case 6:
                    for (int i = n - order; i > 0; i--)
                    {
                        long pred = coefs[5] * (long)*s++;
                        pred += coefs[4] * (long)*s++;
                        pred += coefs[3] * (long)*s++;
                        pred += coefs[2] * (long)*s++;
                        pred += c1 * (long)*s++;
                        pred += c0 * (long)*s++;
                        *s = *r++ + (int)(pred >> shift);
                        s -= 5;
                    }
                    break;
                case 7:
                    for (int i = n - order; i > 0; i--)
                    {
                        long pred = coefs[6] * (long)*s++;
                        pred += coefs[5] * (long)*s++;
                        pred += coefs[4] * (long)*s++;
                        pred += coefs[3] * (long)*s++;
                        pred += coefs[2] * (long)*s++;
                        pred += c1 * (long)*s++;
                        pred += c0 * (long)*s++;
                        *s = *r++ + (int)(pred >> shift);
                        s -= 6;
                    }
                    break;
                case 8:
                    for (int i = n - order; i > 0; i--)
                    {
                        long pred = coefs[7] * (long)*s++;
                        pred += coefs[6] * (long)*s++;
                        pred += coefs[5] * (long)*s++;
                        pred += coefs[4] * (long)*s++;
                        pred += coefs[3] * (long)*s++;
                        pred += coefs[2] * (long)*s++;
                        pred += c1 * (long)*s++;
                        pred += c0 * (long)*s++;
                        *s = *r++ + (int)(pred >> shift);
                        s -= 7;
                    }
                    break;
                default:
                    for (int i = order; i < n; i++)
                    {
                        s = smp + i - order;
                        long pred = 0;
                        int* co = coefs + order - 1;
                        int* c7 = coefs + 7;
                        while (co > c7)
                            pred += *co-- * (long)*s++;
                        pred += coefs[7] * (long)*s++;
                        pred += coefs[6] * (long)*s++;
                        pred += coefs[5] * (long)*s++;
                        pred += coefs[4] * (long)*s++;
                        pred += coefs[3] * (long)*s++;
                        pred += coefs[2] * (long)*s++;
                        pred += c1 * (long)*s++;
                        pred += c0 * (long)*s++;
                        *s = *r++ + (int)(pred >> shift);
                    }
                    break;
            }
        }
    }
}
