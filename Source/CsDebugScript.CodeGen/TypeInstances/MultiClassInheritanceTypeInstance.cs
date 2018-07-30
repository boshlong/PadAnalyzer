﻿namespace CsDebugScript.CodeGen.TypeInstances
{
    /// <summary>
    /// Type instance that represents multiple base classes and that <see cref="UserType"/> should be used.
    /// </summary>
    /// <seealso cref="VariableTypeInstance" />
    internal class MultiClassInheritanceTypeInstance : VariableTypeInstance
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MultiClassInheritanceTypeInstance"/> class.
        /// </summary>
        public MultiClassInheritanceTypeInstance()
            : base(false)
        {
        }
    }
}