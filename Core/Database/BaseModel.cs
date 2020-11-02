﻿using System;
using System.ComponentModel.DataAnnotations;

namespace QuantumCore.Database
{
    public class BaseModel
    {
        [Key]
        public Guid Id { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}