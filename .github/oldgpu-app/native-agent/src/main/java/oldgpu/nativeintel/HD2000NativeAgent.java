package oldgpu.nativeintel;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.IllegalClassFormatException;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Method;
import java.nio.IntBuffer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.IntInsnNode;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.LdcInsnNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;

/**
 * Minecraft 26.3 compatibility agent for the Windows Intel HD Graphics 2000
 * driver (OpenGL 3.1 / GLSL 1.40).
 *
 * It does not emulate OpenGL and does not render on the CPU. It only relaxes
 * RenderPearl's 3.3 gate and redirects the one GL 3.3 entry point whose ARB
 * extension uses a suffixed symbol on Sandy Bridge.
 */
public final class HD2000NativeAgent {
    private static final String PREFIX = "[HD2000 Native] ";

    public static void premain(String agentArgs, Instrumentation inst) {
        System.err.println(PREFIX + "agent active; waiting for Minecraft 26.3 RenderPearl classes");
        inst.addTransformer(new Transformer(), false);
    }

    /**
     * GlDevice only uses SDL_GL_GetAttribute here to reject contexts below 3.3.
     * The actual LWJGL capabilities are still created from the real Intel 3.1
     * context and therefore remain truthful.
     */
    public static boolean spoofContextVersion(int attr, IntBuffer value) {
        boolean ok = invokeSdlGetAttribute(attr, value);
        if (ok && value != null && (attr == 17 || attr == 18)) {
            value.put(0, 3);
        }
        return ok;
    }

    private static boolean invokeSdlGetAttribute(int attr, IntBuffer value) {
        try {
            Class<?> sdlVideo = Class.forName("org.lwjgl.sdl.SDLVideo");
            Method method = sdlVideo.getMethod("SDL_GL_GetAttribute", int.class, IntBuffer.class);
            Object result = method.invoke(null, attr, value);
            return Boolean.TRUE.equals(result);
        } catch (Throwable t) {
            System.err.println(PREFIX + "SDL_GL_GetAttribute bridge failed: " + t);
            return false;
        }
    }

    private static final class Transformer implements ClassFileTransformer {
        private static final String GL_BACKEND = "com/mojang/renderpearl/backend/opengl/GlBackend";
        private static final String GL_DEVICE = "com/mojang/renderpearl/backend/opengl/GlDevice";
        private static final String GL_RECOMPILER = "com/mojang/renderpearl/backend/opengl/GlPipelineRecompiler";
        private static final String VERTEX_ARRAY_EMULATED = "com/mojang/renderpearl/backend/opengl/VertexArray$Emulated";

        @Override
        public byte[] transform(
                ClassLoader loader,
                String className,
                Class<?> classBeingRedefined,
                ProtectionDomain protectionDomain,
                byte[] classfileBuffer) throws IllegalClassFormatException {
            if (className == null) {
                return null;
            }

            try {
                if (GL_BACKEND.equals(className)) {
                    return patchGlBackend(classfileBuffer);
                }
                if (GL_DEVICE.equals(className)) {
                    return patchGlDevice(classfileBuffer);
                }
                if (GL_RECOMPILER.equals(className)) {
                    return patchGlPipelineRecompiler(classfileBuffer);
                }
                if (VERTEX_ARRAY_EMULATED.equals(className)) {
                    return patchVertexArrayEmulated(classfileBuffer);
                }
            } catch (Throwable t) {
                System.err.println(PREFIX + "failed to patch " + className + ": " + t);
                t.printStackTrace(System.err);
            }
            return null;
        }

        private byte[] patchGlBackend(byte[] bytes) {
            ClassNode cn = read(bytes);
            int changed = 0;

            for (MethodNode method : cn.methods) {
                for (AbstractInsnNode insn = method.instructions.getFirst(); insn != null; insn = insn.getNext()) {
                    if (!(insn instanceof MethodInsnNode)) {
                        continue;
                    }
                    MethodInsnNode call = (MethodInsnNode) insn;
                    if (!"org/lwjgl/sdl/SDLVideo".equals(call.owner)
                            || !"SDL_GL_SetAttribute".equals(call.name)
                            || !"(II)Z".equals(call.desc)) {
                        continue;
                    }

                    AbstractInsnNode valueNode = previousReal(insn);
                    AbstractInsnNode attrNode = previousReal(valueNode);
                    Integer attr = intValue(attrNode);
                    Integer value = intValue(valueNode);
                    if (attr == null || value == null) {
                        continue;
                    }

                    // SDL3: 18=minor, 19=flags, 20=profile.
                    // Request a plain 3.1 context: no forward-compatible flag,
                    // and no core-profile requirement (profiles start at GL 3.2).
                    if (attr == 18 && value == 3) {
                        replaceInt(method, valueNode, 1);
                        changed++;
                    } else if (attr == 19 && value != 0) {
                        replaceInt(method, valueNode, 0);
                        changed++;
                    } else if (attr == 20 && value != 0) {
                        replaceInt(method, valueNode, 0);
                        changed++;
                    }
                }
            }

            System.err.println(PREFIX + "GlBackend: " + changed + " context attributes patched for OpenGL 3.1");
            return write(cn);
        }

        private byte[] patchGlDevice(byte[] bytes) {
            ClassNode cn = read(bytes);
            int changed = 0;

            for (MethodNode method : cn.methods) {
                for (AbstractInsnNode insn = method.instructions.getFirst(); insn != null; insn = insn.getNext()) {
                    if (!(insn instanceof MethodInsnNode)) {
                        continue;
                    }
                    MethodInsnNode call = (MethodInsnNode) insn;
                    if ("org/lwjgl/sdl/SDLVideo".equals(call.owner)
                            && "SDL_GL_GetAttribute".equals(call.name)
                            && "(ILjava/nio/IntBuffer;)Z".equals(call.desc)) {
                        call.owner = "oldgpu/nativeintel/HD2000NativeAgent";
                        call.name = "spoofContextVersion";
                        call.itf = false;
                        call.setOpcode(Opcodes.INVOKESTATIC);
                        changed++;
                    }
                }
            }

            System.err.println(PREFIX + "GlDevice: " + changed + " version checks bridged; real GL capabilities stay untouched");
            return write(cn);
        }

        private byte[] patchGlPipelineRecompiler(byte[] bytes) {
            ClassNode cn = read(bytes);
            int changed = 0;

            for (MethodNode method : cn.methods) {
                for (AbstractInsnNode insn = method.instructions.getFirst(); insn != null; insn = insn.getNext()) {
                    if (!(insn instanceof MethodInsnNode)) {
                        continue;
                    }
                    MethodInsnNode call = (MethodInsnNode) insn;
                    if (!"org/lwjgl/util/spvc/Spvc".equals(call.owner)
                            || !"spvc_compiler_options_set_uint".equals(call.name)) {
                        continue;
                    }

                    AbstractInsnNode valueNode = previousReal(insn);
                    AbstractInsnNode optionNode = previousReal(valueNode);
                    Integer option = intValue(optionNode);
                    Integer value = intValue(valueNode);

                    // SPVC_COMPILER_OPTION_GLSL_VERSION (0x02000008)
                    if (option != null && option == 33554440 && value != null && value == 330) {
                        replaceInt(method, valueNode, 140);
                        changed++;
                    }
                }
            }

            System.err.println(PREFIX + "GlPipelineRecompiler: " + changed + " GLSL target(s) changed 330 -> 140");
            return write(cn);
        }

        private byte[] patchVertexArrayEmulated(byte[] bytes) {
            ClassNode cn = read(bytes);
            int changed = 0;

            for (MethodNode method : cn.methods) {
                for (AbstractInsnNode insn = method.instructions.getFirst(); insn != null; insn = insn.getNext()) {
                    if (!(insn instanceof MethodInsnNode)) {
                        continue;
                    }
                    MethodInsnNode call = (MethodInsnNode) insn;
                    if ("org/lwjgl/opengl/GL33C".equals(call.owner)
                            && "glVertexAttribDivisor".equals(call.name)
                            && "(II)V".equals(call.desc)) {
                        // Intel's Sandy Bridge Windows driver exposes
                        // GL_ARB_instanced_arrays with the ARB-suffixed symbol.
                        call.owner = "org/lwjgl/opengl/ARBInstancedArrays";
                        call.name = "glVertexAttribDivisorARB";
                        call.itf = false;
                        call.setOpcode(Opcodes.INVOKESTATIC);
                        changed++;
                    }
                }
            }

            System.err.println(PREFIX + "VertexArray.Emulated: " + changed + " instancing call(s) redirected to ARB");
            return write(cn);
        }

        private static ClassNode read(byte[] bytes) {
            ClassReader reader = new ClassReader(bytes);
            ClassNode node = new ClassNode();
            reader.accept(node, 0);
            return node;
        }

        private static byte[] write(ClassNode node) {
            ClassWriter writer = new ClassWriter(0);
            node.accept(writer);
            return writer.toByteArray();
        }

        private static AbstractInsnNode previousReal(AbstractInsnNode node) {
            if (node == null) {
                return null;
            }
            AbstractInsnNode p = node.getPrevious();
            while (p != null && (p.getType() == AbstractInsnNode.LABEL
                    || p.getType() == AbstractInsnNode.LINE
                    || p.getType() == AbstractInsnNode.FRAME)) {
                p = p.getPrevious();
            }
            return p;
        }

        private static Integer intValue(AbstractInsnNode node) {
            if (node instanceof InsnNode) {
                int op = node.getOpcode();
                if (op >= Opcodes.ICONST_M1 && op <= Opcodes.ICONST_5) {
                    return op - Opcodes.ICONST_0;
                }
            }
            if (node instanceof IntInsnNode) {
                return ((IntInsnNode) node).operand;
            }
            if (node instanceof LdcInsnNode) {
                Object cst = ((LdcInsnNode) node).cst;
                if (cst instanceof Integer) {
                    return (Integer) cst;
                }
            }
            return null;
        }

        private static void replaceInt(MethodNode method, AbstractInsnNode oldNode, int value) {
            AbstractInsnNode replacement;
            if (value >= -1 && value <= 5) {
                replacement = new InsnNode(Opcodes.ICONST_0 + value);
            } else if (value >= Byte.MIN_VALUE && value <= Byte.MAX_VALUE) {
                replacement = new IntInsnNode(Opcodes.BIPUSH, value);
            } else if (value >= Short.MIN_VALUE && value <= Short.MAX_VALUE) {
                replacement = new IntInsnNode(Opcodes.SIPUSH, value);
            } else {
                replacement = new LdcInsnNode(value);
            }
            method.instructions.set(oldNode, replacement);
        }
    }
}
