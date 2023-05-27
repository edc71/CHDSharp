changes:

- removed all static, most of the code is moved to CHD.cs
- ArrayPool.. limit the number of returns to the pool. This makes memory allocation a lot more stable (currently 24) 
	(ArrayPool.cs, line 39)
- deduped blocks in memory. keeping them all makes memory allocation very jumpy.. introduced a threshold (currently 25)
	(CHD.cs, line 66)
- Introduced a arraypools class in CHD.cs, which is passed on when needed to the blockreaders

overall a little bit slower, but more steady memory usage