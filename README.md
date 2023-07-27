# Land Explorer
WPF-based control and Visual Studio extension for speeding up code comprehension and development process, especially when dealing with crosscutting concerns. 
It allows developers to label code fragments (arbitraty ones as well as syntax entities) and quickly navigate to them in a heavily edited code, as well as 
to link more information with the labelled fragments, group the labels and thus create a documentation that is automatically synchronized with the edited code.

Short video tutorial (in Russian): https://yadi.sk/i/Cs_cJGNnxSazsA

The tool uses a context-based code search engine that stores the description of a code fragment as a number of special structures called "contexts"
and then compares this description with descriptions that can be built for code fragments presented in the edited version of the program. This process is also called "robust binding to code".

## Author's research on robust binding to code
* Using improved context-based code description for robust algorithmic binding to changing code, **2021** | _[paper](https://www.sciencedirect.com/science/article/pii/S1877050921020652) & [repo](https://github.com/alexeyvale/YSC-2021)_

* Robust algorithmic binding to arbitrary fragment of program code, **2022** | _[paper](https://psta.psiras.ru/read/psta2022_1_35-62.pdf)_
