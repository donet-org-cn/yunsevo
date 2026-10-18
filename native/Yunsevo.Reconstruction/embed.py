import pathlib,struct,sys
b=pathlib.Path(sys.argv[1]).read_bytes()
pathlib.Path(sys.argv[2]).write_text("#pragma once\nstatic const unsigned int depthShader[]={"+",".join(str(x) for x in struct.unpack("<"+"I"*(len(b)//4),b))+"};\n")
