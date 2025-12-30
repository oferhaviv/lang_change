set TargetFolder=C:\MyBatches\eng_heb

rem === Clean Target Folder ===
del %TargetFolder%\*.* /s /q 

rem === Copy C# file to target ===
xcopy LangChangeToiCUE\bin\Debug\net8.0\LangChangeToiCUE.* %TargetFolder%\*

rem === Copy Python file to target ===
xcopy python_icue\* %TargetFolder%\*

